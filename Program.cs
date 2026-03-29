using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Runtime.InteropServices;
using System.Text;
using WslContainerMcp;
using WslContainerMcp.Runtime;
using WslContainerMcp.Tools;

// ── Parse server flags ────────────────────────────────────────────────────────

bool allowNetwork = !args.Contains("--no-network", StringComparer.OrdinalIgnoreCase);

// ── Directory setup ───────────────────────────────────────────────────────────
//
// Layout under %USERPROFILE%\.wsl-sandbox-mcp\:
//
//   linux-container\          ← full persistent Linux environment (user-browsable)
//     workspace\              ← bind-mounted as /workspace inside the container
//     home\                   ← bind-mounted as /home inside the container
//     out\                    ← per-call metadata JSON files
//   container\
//     Dockerfile              ← auto-extracted; used to build the agent image

var userProfile          = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
var rootWin              = Path.Combine(userProfile, ".wsl-sandbox-mcp");
var linuxContainerWin    = Path.Combine(rootWin, "linux-container");
var workspaceWin         = Path.Combine(linuxContainerWin, "workspace");
var homeWin              = Path.Combine(linuxContainerWin, "home");
var outWin               = Path.Combine(linuxContainerWin, "out");
var containerDirWin      = Path.Combine(rootWin, "container");

string? issueReport = null;

if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
{
    issueReport =
        "❌ This server only runs on Windows.\n\n" +
        "WslContainerMcp requires Windows Subsystem for Linux (WSL) which is a Windows-only feature.\n" +
        "Please run this server on a Windows 10/11 machine.";
    Console.Error.WriteLine("[WslContainerMcp] Not running on Windows.");
}
else
{
    try
    {
        Directory.CreateDirectory(workspaceWin);
        Directory.CreateDirectory(homeWin);
        Directory.CreateDirectory(outWin);
        Directory.CreateDirectory(containerDirWin);

        // Ensure the agent Dockerfile exists in the runtime container directory.
        var dfPath = Path.Combine(containerDirWin, "Dockerfile");
        if (!File.Exists(dfPath))
            File.WriteAllText(dfPath, AgentDockerfile.Content, new UTF8Encoding(false));
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[WslContainerMcp] Failed to create directories: {ex.Message}");
        issueReport =
            "❌ Failed to create the server directories.\n\n" +
            $"Error: {ex.Message}\n\n" +
            "To fix:\n" +
            $"  • Ensure write access to: {rootWin}\n" +
            "  • Try running the server as the same user who will use it.";
    }
}

// ── Environment bootstrap ─────────────────────────────────────────────────────

string  podmanEnv       = "";
string  containerName   = "";
bool    podmanReady     = false;

if (issueReport == null)
{
    // WSL checks — auto-fix where possible, report clearly on failure
    Console.Error.WriteLine("[WslContainerMcp] Checking WSL...");
    string? wslIssue;
    (var wslReady, wslIssue) = await WslBootstrap.EnsureAsync();
    if (!wslReady)
    {
        issueReport = wslIssue;
        Console.Error.WriteLine($"[WslContainerMcp] WSL not ready: {issueReport}");
    }
    else
    {
        Console.Error.WriteLine("[WslContainerMcp] WSL is available. Bootstrapping Podman...");
        string? bootstrapIssue;
        (podmanReady, podmanEnv, containerName, bootstrapIssue) =
            await PodmanBootstrap.RunAsync(containerDirWin, linuxContainerWin, allowNetwork);
        if (!podmanReady)
        {
            issueReport = bootstrapIssue
                ?? "❌ Podman bootstrap failed for an unknown reason. Check the server's error output.";
            Console.Error.WriteLine($"[WslContainerMcp] Podman not ready: {issueReport}");
        }
        else
        {
            Console.Error.WriteLine("[WslContainerMcp] Podman ready. Starting MCP server.");
        }
    }
}

// ── MCP server ────────────────────────────────────────────────────────────────

var bootstrap = new BootstrapResult
{
    PodmanReady             = podmanReady,
    PodmanEnv               = podmanEnv,
    IssueReport             = issueReport,
    LinuxContainerWin       = linuxContainerWin,
    OutWin                  = outWin,
    ContainerDirWin         = containerDirWin,
    PersistentContainerName = containerName,
    AllowNetwork            = allowNetwork,
};

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.AddConsole(opts =>
{
    // Route all logs to stderr so stdout stays clean for the MCP protocol.
    opts.LogToStandardErrorThreshold = LogLevel.Trace;
});

builder.Services.AddSingleton(bootstrap);

var mcp = builder.Services
    .AddMcpServer()
    .WithStdioServerTransport();

if (podmanReady)
    mcp.WithTools<RunLinuxCliTool>();
else
    mcp.WithTools<EnvironmentIssueReportTool>();

await builder.Build().RunAsync();
return 0;

