using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Server;
using WslContainerMcp.Runtime;

namespace WslContainerMcp.Tools;

/// <summary>
/// MCP tool that runs a command inside a persistent Linux container and returns
/// stdout, stderr, and exit code.
/// <para>
/// The container (<c>wsl-sandbox-mcp-persistent</c>) is shared across all tool calls and
/// survives server restarts — installed packages, running processes, and files outside
/// the bind-mounted directories are preserved in the container's overlay layer. Files
/// written to <c>/workspace</c> or <c>/home</c> are immediately accessible from Windows
/// in the <c>linux-container</c> directory at
/// <c>%USERPROFILE%\.wsl-sandbox-mcp\linux-container\</c>.
/// </para>
/// </summary>
[McpServerToolType]
public sealed class RunLinuxCliTool(BootstrapResult bootstrap)
{
    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };

    [McpServerTool(Name = "run_linux_cli")]
    [Description(
        "Run a command inside a persistent Linux container (wsl-sandbox-mcp-persistent). " +
        "The container survives across calls: installed software (e.g. Node.js installed with apt), " +
        "created files, and running background processes are all preserved between invocations. " +
        "Returns stdout, stderr, and exit_code. " +
        "Files written to /workspace or /home inside the container are directly accessible from Windows at " +
        "%USERPROFILE%\\.wsl-sandbox-mcp\\linux-container\\.")]
    public async Task<string> RunAsync(
        [Description("Executable to run inside the container (e.g. \"python3\", \"bash\", \"node\").")]
        string cmd,

        [Description("Arguments to pass to the executable.")]
        string[] args,

        [Description("Working directory inside the container. " +
                     "Accepts an absolute Linux path (e.g. \"/home/user/project\") or a relative path " +
                     "that is resolved under /workspace (e.g. \"myproject\" → /workspace/myproject). " +
                     "Default is \".\" which resolves to /workspace. " +
                     "Paths containing \"..\" are rejected.")]
        string? cwd = null,

        [Description("Timeout in seconds, clamped to the range 1–3600 (default: 120).")]
        int timeout_s = 120,

        [Description("Extra environment variables to set for this command.")]
        Dictionary<string, string>? env = null,

        CancellationToken cancellationToken = default)
    {
        var toolCallId = Guid.NewGuid().ToString("N")[..16];
        var extraEnv   = env ?? new Dictionary<string, string>(StringComparer.Ordinal);

        var result = await LinuxCliRunner.RunAsync(
            toolCallId,
            cmd,
            args,
            cwd,
            timeout_s,
            extraEnv,
            bootstrap.PodmanEnv,
            bootstrap.PersistentContainerName,
            bootstrap.OutWin,
            cancellationToken).ConfigureAwait(false);

        var output = new JsonObject
        {
            ["exit_code"] = result.ExitCode,
            ["stdout"]    = result.Stdout,
            ["stderr"]    = result.Stderr,
            ["timed_out"] = result.TimedOut,
        };
        if (result.ArtifactMeta != null) output["artifact_meta"] = result.ArtifactMeta;

        return output.ToJsonString(IndentedJson);
    }
}

