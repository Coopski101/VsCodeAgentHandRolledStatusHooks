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
        // Capture this thread's ID so the shutdown callback (which runs on a different thread)
        // knows which thread's message queue to post WM_QUIT into
        var threadId = Win32.GetCurrentThreadId();

        // Wrap our callback in a delegate variable to prevent the GC from collecting it
        // while unmanaged code (Windows) still holds a reference to it
        Win32.WinEventDelegate callback = OnForegroundChanged;

        // Subscribe to foreground-change events system-wide.
        // WINEVENT_OUTOFCONTEXT = deliver events via this thread's message queue
        // rather than injecting into the target process.
        // process=0, thread=0 = listen to ALL windows, not just ours.
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

        // When the app shuts down, post WM_QUIT to break out of the GetMessage loop below.
        // PostThreadMessage is the only safe way to stop a message pump from another thread.
        ct.Register(() => Win32.PostThreadMessage(threadId, Win32.WM_QUIT, nint.Zero, nint.Zero));

        // Classic Windows message pump: GetMessage blocks until a message arrives,
        // returns true for normal messages, and false only for WM_QUIT (ending the loop).
        // DispatchMessage routes hook-event messages to our OnForegroundChanged callback.
        while (Win32.GetMessage(out var msg, nint.Zero, 0, 0))
        {
            Win32.TranslateMessage(in msg);
            Win32.DispatchMessage(in msg);
        }

        // Loop exited (WM_QUIT received) — clean up
        Win32.UnhookWinEvent(hook);
        _logger.LogInformation("Foreground detector hook removed");

        // Prevent GC from collecting the delegate before we reach this point.
        // Must come AFTER UnhookWinEvent so the delegate is alive the entire time the hook is active.
        GC.KeepAlive(callback);

        // Signal to ExecuteAsync that this background service has finished
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
                        EventType = BeaconEventType.Clear,
                        Source = BeaconEventSource.Foreground,
                        Reason = "VS Code came to foreground",
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
