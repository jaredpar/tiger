using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using Pipeline.Core;

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.AddConsole(options =>
{
    options.LogToStandardErrorThreshold = LogLevel.Trace;
});

var context = PipelineUtils.CreateContext();
builder.Services.AddSingleton(context);
builder.Services.AddSingleton(HelixClient.Create(context.HelixToken));
builder.Services.AddSingleton(AzdoClient.Create(context.AzureCredential));

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
