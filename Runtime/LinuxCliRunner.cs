using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace WslContainerMcp.Runtime;

/// <summary>
/// Implements the <c>run_linux_cli</c> MCP tool: executes a command inside the persistent
/// Linux container via <c>podman exec</c> and returns stdout, stderr, and exit code.
/// <para>
/// The persistent container (<c>wsl-sandbox-mcp-persistent</c>) survives across tool
/// invocations — installed software, running processes, and filesystem state are preserved
/// exactly as on a normal Linux machine. The agent does not need to know it is in a container.
/// </para>
/// <para>
/// Files written to <c>/workspace</c> or <c>/home</c> inside the container are directly
/// accessible from Windows in the <c>linux-container</c> directory
/// (<c>%USERPROFILE%\.wsl-sandbox-mcp\linux-container\</c>).
/// </para>
/// <para>
/// <b>Long-lived processes:</b> Commands that start background services (e.g. a web server)
/// will keep running in the container after the <c>podman exec</c> call returns only if they
/// are daemonised or redirected. Port exposure from the container to the Windows host requires
/// additional Podman port-forwarding (<c>-p</c>) configured at container creation time, which
/// is not yet wired up automatically. See <see cref="Runtime.PodmanBootstrap"/> for follow-up.
/// </para>
/// </summary>
internal static class LinuxCliRunner
{
    private const string ImageName = "wsl-sandbox-mcp-agent:latest";

    private static readonly char[] ControlChars = { '\n', '\r', '\0' };
    private static string Q(string s) => ProcessExec.Q(s);

    public record RunResult(
        int     ExitCode,
        string  Stdout,
        string  Stderr,
        bool    TimedOut,
        string? ArtifactMeta);

    public static Task<RunResult> RunAsync(
        string            toolCallId,
        string            cmd,
        string[]          args,
        string?           cwdRaw,
        int               timeoutRaw,
        Dictionary<string, string> extraEnv,
        string            podmanEnv,
        string            persistentContainerName,
        string            outWin,
        CancellationToken ct)
        => Task.Run(() => Run(toolCallId, cmd, args, cwdRaw, timeoutRaw, extraEnv, podmanEnv, persistentContainerName, outWin), ct);

    private static RunResult Run(
        string toolCallId,
        string cmd,
        string[] args,
        string? cwdRaw,
        int timeoutRaw,
        Dictionary<string, string> extraEnv,
        string podmanEnv,
        string persistentContainerName,
        string outWin)
    {
        // ── Validate inputs ───────────────────────────────────────────────────
        if (string.IsNullOrWhiteSpace(cmd))
            return Error("cmd is required.");

        if (cmd.IndexOfAny(ControlChars) >= 0)
            return Error("cmd must not contain control characters.");

        if (!PathMapping.TrySanitizeCwd(cwdRaw, out var cwd))
            return Error($"Invalid cwd '{cwdRaw}': must be a relative path or absolute Linux path with no traversal.");

        var timeout = Math.Clamp(timeoutRaw, 1, 3600);

        // ── Resolve working directory ─────────────────────────────────────────
        // Absolute Linux paths are used as-is; relative paths are resolved
        // relative to /workspace (backward-compatible default).
        var workCwd = cwd == "."        ? "/workspace"
                    : cwd.StartsWith('/') ? cwd
                    : $"/workspace/{cwd}";

        var metaRel = $"out/{toolCallId}.meta.json";

        // ── Build podman exec flags ───────────────────────────────────────────
        var envFlags = string.Join(" ",
            extraEnv
                .Where(kv => !string.IsNullOrEmpty(kv.Key) && !kv.Key.Contains('='))
                .Select(kv => $"--env {Q(kv.Key)}={Q(kv.Value)}"));
        var argsStr = string.Join(" ", args.Select(Q));

        var startedTs = DateTimeOffset.UtcNow;

        // ── podman exec ───────────────────────────────────────────────────────
        // Runs the command inside the already-running persistent container.
        // State (installed packages, files outside /workspace and /home) is
        // preserved in the container's overlay layer across calls.
        var execParts = new[]
        {
            podmanEnv,
            "podman exec",
            $"-w {Q(workCwd)}",
            envFlags,
            Q(persistentContainerName),
            Q(cmd),
            argsStr,
        };
        var execScript = string.Join(" ", execParts.Where(p => !string.IsNullOrEmpty(p))).Trim();

        var execResult = ProcessExec.WslSh(execScript, timeout + 5);

        bool timedOut = execResult.exitCode == -1 &&
                        execResult.stderr.Contains("Timeout", StringComparison.OrdinalIgnoreCase);
        if (timedOut)
        {
            Console.Error.WriteLine($"[LinuxCliRunner] Timeout ({timeout}s) hit; command may still be running inside '{persistentContainerName}'.");
        }

        var finishedTs = DateTimeOffset.UtcNow;

        // ── Write meta JSON ───────────────────────────────────────────────────
        string? artifactMeta = null;
        try
        {
            var meta = new JsonObject
            {
                ["container"]   = persistentContainerName,
                ["cmd"]         = cmd,
                ["args"]        = new JsonArray(args.Select(a => (JsonNode)JsonValue.Create(a)!).ToArray()),
                ["cwd"]         = workCwd,
                ["env"]         = new JsonObject(
                    extraEnv.Select(kv =>
                        new KeyValuePair<string, JsonNode?>(kv.Key, JsonValue.Create(kv.Value)))),
                ["exit_code"]   = execResult.exitCode,
                ["timed_out"]   = timedOut,
                ["started_ts"]  = startedTs.ToString("O"),
                ["finished_ts"] = finishedTs.ToString("O"),
            };

            File.WriteAllText(
                Path.Combine(outWin, $"{toolCallId}.meta.json"),
                meta.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
                new System.Text.UTF8Encoding(false));
            artifactMeta = metaRel;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[LinuxCliRunner] meta write failed: {ex.Message}");
        }

        return new RunResult(
            execResult.exitCode,
            execResult.stdout,
            execResult.stderr,
            timedOut,
            artifactMeta);
    }

    private static RunResult Error(string message) =>
        new RunResult(-1, "", message, false, null);
}
