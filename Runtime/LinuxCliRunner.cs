using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace WslContainerMcp.Runtime;

/// <summary>
/// Implements the <c>run_linux_cli</c> MCP tool against a single <b>persistent</b> Podman
/// container (<c>wsl-sandbox-mcp-persistent</c>) that is reused across all tool invocations.
/// State — installed packages, cloned repositories, shell history — survives between calls
/// and across server restarts, giving the agent the experience of a normal Linux machine.
///
/// Files written under <c>/workspace</c> inside the container are directly visible from Windows
/// at <c>%USERPROFILE%\.wsl-sandbox-mcp\workspace\</c>.
/// Files written under <c>/root</c> (the root user's home directory) are directly visible from
/// Windows at <c>%USERPROFILE%\.wsl-sandbox-mcp\home\</c>.
/// Software installed via <c>apt-get</c> or other package managers persists in the container's
/// overlay filesystem and is accessible via <c>\\wsl$\&lt;distro&gt;\…\podman\graphroot\</c>.
///
/// To reset the environment (e.g. after a Dockerfile rebuild or to start clean), stop and
/// remove the container from a WSL shell:
///   podman stop wsl-sandbox-mcp-persistent
///   podman rm   wsl-sandbox-mcp-persistent
/// Then restart this server; it will recreate the container automatically.
///
/// Long-running background processes (e.g. <c>npm run dev</c>) and port exposure to the
/// Windows host are not managed by this runner. Run such processes with <c>podman exec -d</c>
/// from a WSL shell, or use <c>nohup</c>/<c>screen</c>/<c>tmux</c> inside the container.
/// Port mapping (e.g. <c>-p 3000:3000</c>) must be configured when the container is first
/// created; see the README for instructions.
/// </summary>
internal static class LinuxCliRunner
{
    private const string ImageName     = "wsl-sandbox-mcp-agent:latest";
    private const string ContainerName = "wsl-sandbox-mcp-persistent";

    /// <summary>Serialises container create/start so concurrent tool calls do not race.</summary>
    private static readonly object EnsureLock = new();

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
        bool              allowNetwork,
        string            workspaceWin,
        string            homeWin,
        string            outWin,
        CancellationToken ct)
        => Task.Run(() => Run(toolCallId, cmd, args, cwdRaw, timeoutRaw, extraEnv, podmanEnv, allowNetwork, workspaceWin, homeWin, outWin), ct);

    private static RunResult Run(
        string toolCallId,
        string cmd,
        string[] args,
        string? cwdRaw,
        int timeoutRaw,
        Dictionary<string, string> extraEnv,
        string podmanEnv,
        bool allowNetwork,
        string workspaceWin,
        string homeWin,
        string outWin)
    {
        // ── Validate inputs ───────────────────────────────────────────────────
        if (string.IsNullOrWhiteSpace(cmd))
            return Error("cmd is required.");

        if (cmd.IndexOfAny(ControlChars) >= 0)
            return Error("cmd must not contain control characters.");

        // ── Determine working directory inside the container ──────────────────
        // Absolute Linux paths (starting with '/') are used as-is, allowing the
        // agent to work anywhere in the container (e.g. /root/myproject).
        // Relative paths are resolved against /workspace for backward compatibility.
        string workCwd;
        var cwdTrimmed = cwdRaw?.Trim();
        if (!string.IsNullOrEmpty(cwdTrimmed) && cwdTrimmed.StartsWith('/'))
        {
            // Splitting on '/' catches ".." as a standalone segment in paths like
            // "/foo/../bar" (→ segments ["", "foo", "..", "bar"]) as well as bare "..".
            if (cwdTrimmed.Split('/').Any(s => s == ".."))
                return Error($"Invalid cwd '{cwdRaw}': must not contain '..' traversal.");
            workCwd = cwdTrimmed;
        }
        else
        {
            if (!PathMapping.TrySanitizeCwd(cwdRaw, out var relCwd))
                return Error($"Invalid cwd '{cwdRaw}': must be a relative path with no traversal.");
            workCwd = relCwd == "." ? "/workspace" : $"/workspace/{relCwd}";
        }

        var timeout = Math.Clamp(timeoutRaw, 1, 3600);

        // ── Resolve Windows → WSL paths ───────────────────────────────────────
        var wslWorkspace = PathMapping.ToWslPath(workspaceWin);
        if (string.IsNullOrWhiteSpace(wslWorkspace))
            return Error("Cannot map workspace path to WSL /mnt/... path.");

        var wslHome = PathMapping.ToWslPath(homeWin);
        if (string.IsNullOrWhiteSpace(wslHome))
            return Error("Cannot map home path to WSL /mnt/... path.");

        var metaRel = $"out/{toolCallId}.meta.json";

        // ── Ensure the persistent container is running ────────────────────────
        var (ensureOk, ensureMsg) = EnsureRunning(podmanEnv, allowNetwork, wslWorkspace, wslHome);
        if (!ensureOk)
            return Error($"Could not start persistent container: {ensureMsg}");

        // ── Build podman exec command ─────────────────────────────────────────
        var envFlags = string.Join(" ",
            extraEnv
                .Where(kv => !string.IsNullOrEmpty(kv.Key) && !kv.Key.Contains('='))
                .Select(kv => $"--env {Q(kv.Key)}={Q(kv.Value)}"));
        var argsStr = string.Join(" ", args.Select(Q));

        var startedTs = DateTimeOffset.UtcNow;

        // ── podman exec ───────────────────────────────────────────────────────
        var execParts = new[]
        {
            podmanEnv,
            "podman exec",
            $"-w {Q(workCwd)}",
            envFlags,
            Q(ContainerName),
            Q(cmd),
            argsStr,
        };
        var execScript = string.Join(" ", execParts.Where(p => !string.IsNullOrEmpty(p))).Trim();

        var execResult = ProcessExec.WslSh(execScript, timeout + 5);

        bool timedOut = execResult.exitCode == -1 &&
                        execResult.stderr.Contains("Timeout", StringComparison.OrdinalIgnoreCase);
        if (timedOut)
        {
            // The container keeps running after a timeout; killing the exec process only
            // detaches our I/O from the command — the command may still be alive inside
            // the container. If this happens repeatedly, zombie processes can accumulate.
            // The user can inspect and clean up with:
            //   podman exec wsl-sandbox-mcp-persistent ps aux
            // Future work: track PIDs and expose a cleanup tool.
            Console.Error.WriteLine(
                $"[LinuxCliRunner] Timeout ({timeout}s) hit for command '{cmd}' in {ContainerName}. " +
                "The container continues running; the timed-out process may still be alive inside it.");
        }

        var finishedTs = DateTimeOffset.UtcNow;

        // ── Write meta JSON ───────────────────────────────────────────────────
        string? artifactMeta = null;
        try
        {
            var meta = new JsonObject
            {
                ["container"]   = ContainerName,
                ["image"]       = ImageName,
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

    /// <summary>
    /// Ensures the persistent container is running. Thread-safe: concurrent callers are
    /// serialised so only one create/start operation runs at a time.
    /// <list type="bullet">
    ///   <item>If the container is already running → no-op.</item>
    ///   <item>If the container exists but is stopped → <c>podman start</c>.</item>
    ///   <item>If the container does not exist → <c>podman run -d … sleep infinity</c>.</item>
    /// </list>
    /// Note: network mode and volume mounts are fixed at creation time. If the server is
    /// restarted with a different <c>--no-network</c> flag, the existing container keeps its
    /// original network setting. To apply new settings, stop and remove the container first
    /// (see the class-level XML summary for the commands).
    /// </summary>
    private static (bool ok, string message) EnsureRunning(
        string podmanEnv, bool allowNetwork, string wslWorkspace, string wslHome)
    {
        lock (EnsureLock)
        {
            // Query the current state of the named container.
            // Exit 0 + "true"  → running.
            // Exit 0 + "false" → exists but stopped.
            // Non-zero exit    → container does not exist.
            var inspectResult = ProcessExec.WslSh(
                $"{podmanEnv} podman container inspect {Q(ContainerName)} --format '{{{{.State.Running}}}}'",
                15);

            if (inspectResult.exitCode == 0)
            {
                if (inspectResult.stdout.Trim() == "true")
                    return (true, ""); // Already running — nothing to do.

                // Container exists but is stopped (e.g. after a machine reboot).
                Console.Error.WriteLine(
                    $"[LinuxCliRunner] Persistent container '{ContainerName}' is stopped; restarting it.");
                var startResult = ProcessExec.WslSh(
                    $"{podmanEnv} podman start {Q(ContainerName)}", 30);
                if (startResult.exitCode != 0)
                    return (false, $"podman start failed (exit {startResult.exitCode}): {startResult.stderr.Trim()}");

                return (true, "");
            }

            // Container does not exist — create it.
            // /workspace  → project files, directly visible from Windows
            // /root       → root user's home directory, directly visible from Windows
            // sleep infinity keeps the container alive indefinitely so every tool call
            // re-uses the same environment without losing installed packages or state.
            Console.Error.WriteLine(
                $"[LinuxCliRunner] Creating persistent container '{ContainerName}'.");

            var networkFlag = allowNetwork ? "" : "--network none";
            var createParts = new[]
            {
                podmanEnv,
                "podman run -d",
                $"--name {Q(ContainerName)}",
                networkFlag,
                $"-v {Q(wslWorkspace + ":/workspace:rw")}",
                $"-v {Q(wslHome + ":/root:rw")}",
                "-e HOME=/root",
                "-w /workspace",
                Q(ImageName),
                "sleep infinity",
            };
            var createScript = string.Join(" ", createParts.Where(p => !string.IsNullOrEmpty(p))).Trim();

            var createResult = ProcessExec.WslSh(createScript, 60);
            if (createResult.exitCode != 0)
            {
                // Another process may have raced us and already created the container.
                // Re-inspect before giving up.
                var recheck = ProcessExec.WslSh(
                    $"{podmanEnv} podman container inspect {Q(ContainerName)} --format '{{{{.State.Running}}}}'",
                    15);
                if (recheck.exitCode == 0)
                    return (true, ""); // Someone else created it — all good.

                return (false, $"podman run failed (exit {createResult.exitCode}): {createResult.stderr.Trim()}");
            }

            Console.Error.WriteLine(
                $"[LinuxCliRunner] Persistent container '{ContainerName}' created and started.");
            return (true, "");
        }
    }

    private static RunResult Error(string message) =>
        new RunResult(-1, "", message, false, null);
}

