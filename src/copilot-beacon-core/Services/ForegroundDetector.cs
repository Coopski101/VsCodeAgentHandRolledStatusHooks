using System.Diagnostics;
using CopilotBeaconCore.Config;
using CopilotBeaconCore.Events;
using CopilotBeaconCore.Native;

namespace CopilotBeaconCore.Services;

public sealed class ForegroundDetector : BackgroundService
{
    private readonly EventBus _bus;
    private readonly CoreConfig _config;
    private readonly ILogger<ForegroundDetector> _logger;

    public bool IsVsCodeForeground { get; private set; }

    public ForegroundDetector(EventBus bus, CoreConfig config, ILogger<ForegroundDetector> logger)
    {
        _bus = bus;
        _config = config;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var tcs = new TaskCompletionSource();
        var thread = new Thread(() => RunMessageLoop(tcs, stoppingToken))
        {
            IsBackground = true,
            Name = "ForegroundDetector-MessagePump",
        };
        thread.Start();
        return tcs.Task;
    }

    private void RunMessageLoop(TaskCompletionSource tcs, CancellationToken ct)
    {
        var threadId = Win32.GetCurrentThreadId();

        Win32.WinEventDelegate callback = OnForegroundChanged;
        var hook = Win32.SetWinEventHook(
            Win32.EVENT_SYSTEM_FOREGROUND,
            Win32.EVENT_SYSTEM_FOREGROUND,
            nint.Zero,
            callback,
            0,
            0,
            Win32.WINEVENT_OUTOFCONTEXT
        );

        if (hook == nint.Zero)
        {
            _logger.LogError("SetWinEventHook failed");
            tcs.SetResult();
            return;
        }

        _logger.LogInformation("Foreground detector hook installed");

        ct.Register(() => Win32.PostThreadMessage(threadId, Win32.WM_QUIT, nint.Zero, nint.Zero));

        while (Win32.GetMessage(out var msg, nint.Zero, 0, 0))
        {
            Win32.TranslateMessage(in msg);
            Win32.DispatchMessage(in msg);
        }

        Win32.UnhookWinEvent(hook);
        _logger.LogInformation("Foreground detector hook removed");
        GC.KeepAlive(callback);
        tcs.SetResult();
    }

    private void OnForegroundChanged(
        nint hWinEventHook,
        uint eventType,
        nint hwnd,
        int idObject,
        int idChild,
        uint dwEventThread,
        uint dwmsEventTime
    )
    {
        try
        {
            Win32.GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0)
                return;

            var process = Process.GetProcessById((int)pid);
            var isVsCode = process.ProcessName.Equals(
                _config.VscodeProcessName,
                StringComparison.OrdinalIgnoreCase
            );

            IsVsCodeForeground = isVsCode;

            if (isVsCode)
            {
                _logger.LogDebug("VS Code came to foreground");
                _bus.Publish(
                    new CopilotEvent
                    {
                        EventName = "copilot.clear",
                        Payload = new ClearPayload { Reason = "vscode_foreground" },
                    }
                );
            }
        }
        catch (Exception ex)
        {
            _logger.LogTrace(ex, "Error in foreground callback");
        }
    }
}
