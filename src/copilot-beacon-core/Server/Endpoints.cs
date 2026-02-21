using CopilotBeaconCore.Events;

namespace CopilotBeaconCore.Server;

public static class Endpoints
{
    public static void MapCoreEndpoints(this WebApplication app, EventBus bus)
    {
        app.MapGet("/events", (HttpContext ctx) => SseHandler.HandleSseConnection(ctx, bus));

        app.MapGet("/health", () => Results.Json(new { ok = true, version = "0.1.0" }));

        app.MapGet(
            "/state",
            () => Results.Json(new { active = bus.ActiveSignal, mode = bus.CurrentMode })
        );
    }
}
