using CopilotBeaconCore.Config;
using CopilotBeaconCore.Events;
using CopilotBeaconCore.Server;
using CopilotBeaconCore.Services;

var config = new CoreConfig();

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile("core.config.json", optional: true);
builder.Configuration.GetSection("Core").Bind(config);

builder.WebHost.UseUrls($"http://127.0.0.1:{config.Port}");

var bus = new EventBus();
builder.Services.AddSingleton(bus);
builder.Services.AddHostedService<FakeEventEmitter>();

var app = builder.Build();

app.MapCoreEndpoints(bus);

Console.WriteLine($"copilot-beacon-core v0.1.0 listening on http://127.0.0.1:{config.Port}");
Console.WriteLine("Endpoints: /events (SSE), /health, /state");
Console.WriteLine("Fake event emitter active — cycling every 10s");

app.Run();
