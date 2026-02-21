using CopilotBeaconCore.Config;
using CopilotBeaconCore.Events;
using CopilotBeaconCore.Native;

namespace CopilotBeaconCore.Services;

public sealed class AfkDetector : BackgroundService
{
    private readonly EventBus _bus;
    private readonly CoreConfig _config;
    private readonly ForegroundDetector _foreground;
    private readonly ILogger<AfkDetector> _logger;

    private bool _wasAfk;
    private int _consecutiveFailures;

    public AfkDetector(
        EventBus bus,
        CoreConfig config,
        ForegroundDetector foreground,
        ILogger<AfkDetector> logger
    )
    {
        _bus = bus;
        _config = config;
        _foreground = foreground;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "AFK detector started (threshold={Threshold}s, poll={Poll}ms)",
            _config.AfkThresholdSeconds,
            _config.PollIntervalMs
        );

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(_config.PollIntervalMs, stoppingToken);

            var idleMs = GetIdleTimeMs();
            if (idleMs < 0)
            {
                _consecutiveFailures++;
                if (_consecutiveFailures == 20)
                    _logger.LogWarning("GetLastInputInfo has failed {Count} times in a row — AFK detection may be non-functional", _consecutiveFailures);
                continue;
            }
            _consecutiveFailures = 0;

            var thresholdMs = _config.AfkThresholdSeconds * 1000;

            if (idleMs >= thresholdMs)
            {
                if (!_wasAfk)
                {
                    _logger.LogDebug("User went AFK (idle {Idle}ms)", idleMs);
                    _wasAfk = true;
                }
            }
            else if (_wasAfk && idleMs <= 1000)
            {
                _logger.LogDebug("User returned from AFK (idle {Idle}ms)", idleMs);
                _wasAfk = false;

                if (_foreground.IsVsCodeForeground)
                {
                    _bus.Publish(
                        new CopilotEvent
                        {
                            EventName = "copilot.clear",
                            Payload = new ClearPayload
                            {
                                Reason = "afk_return_while_vscode_foreground",
                            },
                        }
                    );
                }
            }
        }
    }

    private static long GetIdleTimeMs()
    {
        var info = new Win32.LASTINPUTINFO { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Win32.LASTINPUTINFO>() };
        if (!Win32.GetLastInputInfo(ref info))
            return -1;
        return Environment.TickCount - (int)info.dwTime;
    }
}
