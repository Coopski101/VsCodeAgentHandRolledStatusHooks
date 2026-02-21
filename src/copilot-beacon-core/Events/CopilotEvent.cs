using System.Text.Json.Serialization;

namespace CopilotBeaconCore.Events;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BeaconEventType
{
    Waiting,
    Done,
    Clear,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BeaconEventSource
{
    Toast,
    Pane,
    Foreground,
    Afk,
    Fake,
}

public sealed class CopilotEvent
{
    [JsonPropertyName("event")]
    public required BeaconEventType EventType { get; init; }

    [JsonPropertyName("source")]
    public required BeaconEventSource Source { get; init; }

    [JsonPropertyName("timestamp")]
    public string Timestamp { get; init; } = DateTime.UtcNow.ToString("o");

    [JsonPropertyName("reason")]
    public required string Reason { get; init; }
}
