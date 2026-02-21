using System.Text.Json.Serialization;

namespace CopilotBeaconCore.Events;

public sealed class CopilotEvent
{
    [JsonPropertyName("event")]
    public required string EventName { get; init; }

    [JsonPropertyName("timestamp")]
    public string Timestamp { get; init; } = DateTime.UtcNow.ToString("o");

    [JsonPropertyName("payload")]
    public required object Payload { get; init; }
}

public sealed class ToastPayload
{
    [JsonPropertyName("rawText")]
    public required string RawText { get; init; }

    [JsonPropertyName("confidence")]
    public double Confidence { get; init; } = 1.0;

    [JsonPropertyName("source")]
    public string Source { get; init; } = "toast";
}

public sealed class ClearPayload
{
    [JsonPropertyName("reason")]
    public required string Reason { get; init; }
}
