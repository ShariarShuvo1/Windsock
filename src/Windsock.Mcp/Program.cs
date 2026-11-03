using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Windsock.Core.Feed;

// Standard output is the protocol's channel, so nothing else may write there:
// no console logging at all.
HostApplicationBuilder builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ApplicationName = "Windsock.Mcp",
    ContentRootPath = AppContext.BaseDirectory,
});

builder.Logging.ClearProviders();
builder.Services.AddSingleton(_ => FeedClient.FromDataFolder());
builder.Services
    .AddMcpServer(options => options.ServerInfo = new() { Name = "windsock", Version = LocalFeed.VersionOf(typeof(Program).Assembly) })
    .WithStdioServerTransport()
    .WithTools<Windsock.Mcp.WindsockTools>();

await builder.Build().RunAsync().ConfigureAwait(false);
