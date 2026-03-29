using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Runtime.InteropServices;
using System.Text;
using WslContainerMcp;
using WslContainerMcp.Runtime;
using WslContainerMcp.Tools;

if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
{
    Console.Error.WriteLine("[WslContainerMcp] This server requires Windows.");
    return 1;
}

// ── Workspace setup ───────────────────────────────────────────────────────────

var userProfile     = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
var rootWin         = Path.Combine(userProfile, ".wsl-sandbox-mcp");
var workspaceWin    = Path.Combine(rootWin, "workspace");
var outWin          = Path.Combine(workspaceWin, "out");
var containerDirWin = Path.Combine(rootWin, "container");

try
{
    Directory.CreateDirectory(outWin);
    Directory.CreateDirectory(containerDirWin);

    // Ensure the agent Dockerfile exists in the runtime container directory.
    var dfPath = Path.Combine(containerDirWin, "Dockerfile");
    if (!File.Exists(dfPath))
        File.WriteAllText(dfPath, AgentDockerfile.Content, new UTF8Encoding(false));
}
catch (Exception ex)
{
    Console.Error.WriteLine($"[WslContainerMcp] Failed to create workspace dirs: {ex.Message}");
    return 1;
}

// ── Environment bootstrap ─────────────────────────────────────────────────────

string? issueReport = null;
string  podmanEnv   = "";
bool    podmanReady = false;

if (!WslProbe.IsWslCallable())
{
    issueReport =
        "WSL_NOT_AVAILABLE: wsl.exe is not callable or returned a non-zero exit code.\n" +
        "To fix: install WSL via 'wsl --install' or enable it in Windows Features.\n" +
        "Reference: https://aka.ms/wslinstall";
    Console.Error.WriteLine($"[WslContainerMcp] {issueReport}");
}
else if (!WslProbe.HasAnyDistro())
{
    issueReport =
        "WSL_NO_DISTRO: WSL is installed but no Linux distribution is registered.\n" +
        "To fix: run 'wsl --install' or install a distro from the Microsoft Store.";
    Console.Error.WriteLine($"[WslContainerMcp] {issueReport}");
}
else if (!WslProbe.CanRunShell())
{
    issueReport =
        "WSL_SHELL_FAILED: WSL is present but shell execution failed " +
        "(wsl -e sh -lc \"echo ok\" did not return 'ok').\n" +
        "To fix: ensure your default WSL distro is healthy ('wsl --status').";
    Console.Error.WriteLine($"[WslContainerMcp] {issueReport}");
}
else
{
    Console.Error.WriteLine("[WslContainerMcp] WSL is available. Bootstrapping Podman...");
    string? bootstrapIssue;
    (podmanReady, podmanEnv, bootstrapIssue) = await PodmanBootstrap.RunAsync(containerDirWin);
    if (!podmanReady)
    {
        issueReport = bootstrapIssue
            ?? "PODMAN_NOT_READY: Podman bootstrap failed for an unknown reason. Check stderr.";
        Console.Error.WriteLine($"[WslContainerMcp] Podman not ready: {issueReport}");
    }
    else
    {
        Console.Error.WriteLine("[WslContainerMcp] Podman ready. Starting MCP server.");
    }
}

// ── MCP server ────────────────────────────────────────────────────────────────

var bootstrap = new BootstrapResult
{
    PodmanReady     = podmanReady,
    PodmanEnv       = podmanEnv,
    IssueReport     = issueReport,
    WorkspaceWin    = workspaceWin,
    OutWin          = outWin,
    ContainerDirWin = containerDirWin,
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
