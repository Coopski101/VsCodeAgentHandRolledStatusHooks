using CopilotBeaconCore.Config;
using CopilotBeaconCore.Events;
using CopilotBeaconCore.Server;
using CopilotBeaconCore.Services;

var config = new CoreConfig();

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile("core.config.json", optional: true);
builder.Configuration.GetSection("Core").Bind(config);

var envFake = Environment.GetEnvironmentVariable("BEACON_FAKE_MODE");
if (envFake is "1" or "true")
    config.FakeMode = true;

builder.WebHost.UseUrls($"http://127.0.0.1:{config.Port}");

var bus = new EventBus();
builder.Services.AddSingleton(bus);
builder.Services.AddSingleton(config);

if (config.FakeMode)
{
    builder.Services.AddHostedService<FakeEventEmitter>();
}
else
{
    builder.Services.AddSingleton<ForegroundDetector>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<ForegroundDetector>());
    builder.Services.AddHostedService<AfkDetector>();
    builder.Services.AddHostedService<ToastDetector>();
}

var app = builder.Build();

app.MapCoreEndpoints(bus);

Console.WriteLine($"copilot-beacon-core v0.2.0 listening on http://127.0.0.1:{config.Port}");
Console.WriteLine("Endpoints: /events (SSE), /health, /state");
Console.WriteLine(config.FakeMode ? "Mode: FAKE (cycling events every 10s)" : "Mode: LIVE (toast + foreground + AFK detection)");

app.Run();
