using Agent.Service;

var builder = Host.CreateApplicationBuilder(args);

// Runs as a real Windows service when launched by the Service Control
// Manager (install.ps1 registers it that way); falls back to running
// interactively in a console when launched directly, which is the
// easiest way to test changes without reinstalling the service.
builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "WsusClientAgent";
});

builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
