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
/// MCP tool that runs a command inside the persistent Linux container
/// (<c>wsl-sandbox-mcp-persistent</c>) and returns stdout, stderr, and exit code.
/// The container is created once and reused across all tool calls and server restarts,
/// so installed software, cloned repositories, and other state persist between invocations.
///
/// Two directories inside the container are directly accessible from Windows:
/// <list type="bullet">
///   <item><c>/workspace</c> → <c>%USERPROFILE%\.wsl-sandbox-mcp\workspace\</c></item>
///   <item><c>/root</c> (home) → <c>%USERPROFILE%\.wsl-sandbox-mcp\home\</c></item>
/// </list>
/// </summary>
[McpServerToolType]
public sealed class RunLinuxCliTool(BootstrapResult bootstrap)
{
    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };

    [McpServerTool(Name = "run_linux_cli")]
    [Description(
        "Run a command inside a persistent Linux container (wsl-sandbox-mcp-agent:latest). " +
        "The container is reused across all calls, so installed packages, cloned repositories, " +
        "and any other state survive between invocations — the environment behaves like a normal Linux machine. " +
        "Returns stdout, stderr, and exit_code. " +
        "Files written to /workspace are visible from Windows at %USERPROFILE%\\.wsl-sandbox-mcp\\workspace\\. " +
        "Files written to /root (the home directory) are visible from Windows at %USERPROFILE%\\.wsl-sandbox-mcp\\home\\. " +
        "Note: long-running background processes (e.g. dev servers) are not automatically managed; " +
        "use nohup, screen, or tmux inside the container if you need them to persist after a call returns.")]
    public async Task<string> RunAsync(
        [Description("Executable to run inside the container (e.g. \"python3\", \"bash\", \"node\").")]
        string cmd,

        [Description("Arguments to pass to the executable.")]
        string[] args,

        [Description(
            "Working directory for the command. " +
            "Absolute Linux paths (e.g. \"/root/myproject\") are used as-is. " +
            "Relative paths are resolved from /workspace (default: \".\"). " +
            "Absolute paths must not contain \"..\" traversal; relative paths must not start with \"/\" or contain \"..\" or drive letters.")]
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
            bootstrap.AllowNetwork,
            bootstrap.WorkspaceWin,
            bootstrap.HomeWin,
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

