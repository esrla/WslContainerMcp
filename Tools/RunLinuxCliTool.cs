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
/// MCP tool that runs a command inside a disposable Podman Linux container
/// and returns stdout, stderr, exit code, and artifact paths (Mode B).
/// </summary>
[McpServerToolType]
public sealed class RunLinuxCliTool(BootstrapResult bootstrap)
{
    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };

    [McpServerTool(Name = "run_linux_cli")]
    [Description(
        "Run a command inside a disposable Podman Linux container (wsl-sandbox-mcp-agent:latest). " +
        "Returns stdout, stderr, exit_code, and paths to the exported container filesystem tar " +
        "and a metadata JSON (Mode B artifacts).")]
    public async Task<string> RunAsync(
        [Description("Executable to run inside the container (e.g. \"python3\", \"bash\").")]
        string cmd,

        [Description("Arguments to pass to the executable.")]
        string[] args,

        [Description("Working directory relative to the workspace root (default: \".\"). " +
                     "Must be a relative path; absolute paths, drive prefixes, and \"..\" segments are rejected.")]
        string? cwd = null,

        [Description("Timeout in seconds, clamped to the range 1–3600 (default: 120).")]
        int timeout_s = 120,

        [Description("Extra environment variables to set inside the container.")]
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
            bootstrap.WorkspaceWin,
            bootstrap.OutWin,
            cancellationToken).ConfigureAwait(false);

        var output = new JsonObject
        {
            ["exit_code"] = result.ExitCode,
            ["stdout"]    = result.Stdout,
            ["stderr"]    = result.Stderr,
            ["timed_out"] = result.TimedOut,
        };
        if (result.ArtifactTar  != null) output["artifact_tar"]  = result.ArtifactTar;
        if (result.ArtifactMeta != null) output["artifact_meta"] = result.ArtifactMeta;

        return output.ToJsonString(IndentedJson);
    }
}
