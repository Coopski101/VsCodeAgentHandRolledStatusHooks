using System.Threading.Channels;

namespace CopilotBeaconCore.Events;

public sealed class EventBus
{
    private readonly List<Channel<CopilotEvent>> _subscribers = [];
    private readonly Lock _lock = new();

    public string CurrentMode { get; private set; } = "idle";
    public bool ActiveSignal { get; private set; }

    public ChannelReader<CopilotEvent> Subscribe()
    {
        var channel = Channel.CreateUnbounded<CopilotEvent>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false }
        );

        lock (_lock)
        {
            _subscribers.Add(channel);
        }

        return channel.Reader;
    }

    public void Unsubscribe(ChannelReader<CopilotEvent> reader)
    {
        lock (_lock)
        {
            _subscribers.RemoveAll(ch => ch.Reader == reader);
        }
    }

    public void Publish(CopilotEvent evt)
    {
        if (evt.EventType is BeaconEventType.Waiting or BeaconEventType.Done)
        {
            ActiveSignal = true;
            CurrentMode = evt.EventType == BeaconEventType.Waiting ? "waiting" : "done";
        }
        else if (evt.EventType == BeaconEventType.Clear)
        {
            if (!ActiveSignal)
                return;
            ActiveSignal = false;
            CurrentMode = "idle";
        }

        lock (_lock)
        {
            foreach (var ch in _subscribers)
            {
                ch.Writer.TryWrite(evt);
            }
        }
    }
}
