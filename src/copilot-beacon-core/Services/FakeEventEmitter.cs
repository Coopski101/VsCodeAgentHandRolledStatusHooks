using CopilotBeaconCore.Events;

namespace CopilotBeaconCore.Services;

public sealed class FakeEventEmitter : BackgroundService
{
    private readonly EventBus _bus;
    private readonly ILogger<FakeEventEmitter> _logger;

    public FakeEventEmitter(EventBus bus, ILogger<FakeEventEmitter> logger)
    {
        _bus = bus;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("FakeEventEmitter started — cycling events every 10s");

        var sequence = new[]
        {
            (
                "copilot.waiting",
                (object)
                    new ToastPayload
                    {
                        RawText = "[fake] Copilot is waiting for approval",
                        Confidence = 1.0,
                    }
            ),
            (
                "copilot.done",
                (object)
                    new ToastPayload { RawText = "[fake] Copilot has finished", Confidence = 1.0 }
            ),
            ("copilot.clear", (object)new ClearPayload { Reason = "vscode_foreground" }),
        };

        var index = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(10_000, stoppingToken);

            var (name, payload) = sequence[index % sequence.Length];
            var evt = new CopilotEvent { EventName = name, Payload = payload };

            _logger.LogInformation("Emitting fake event: {Event}", name);
            _bus.Publish(evt);
            index++;
        }
    }
}
