using DokuWebMcp.Db;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);

// Connection string lives in user secrets locally (never in git).
// Environment variable DOKUWEB_ConnectionStrings__DokuWeb also works.
builder.Configuration.AddUserSecrets<Program>(optional: true);
builder.Configuration.AddEnvironmentVariables(prefix: "DOKUWEB_");

// stdout is the MCP transport - all logging must go to stderr.
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services.AddSingleton<Database>();

builder.Services
    .AddMcpServer(o =>
    {
        o.ServerInfo = new() { Name = "dokuweb", Version = "0.1.0" };
    })
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
