using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using VesperNet.Service;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddWindowsService(options =>
{
    options.ServiceName = VesperNetControlService.ServiceName;
});
builder.Services.AddHostedService<VesperNetControlService>();

using var host = builder.Build();
await host.RunAsync();
