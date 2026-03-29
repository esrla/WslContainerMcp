using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace WslContainerMcp.Runtime;

/// <summary>Implements the <c>run_linux_cli</c> MCP tool: creates a container, runs a command,
/// writes a meta JSON, and removes the container. Files written to /workspace inside the
/// container are directly accessible from Windows via the mounted workspace directory.</summary>
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
        bool              allowNetwork,
        string            workspaceWin,
        string            outWin,
        CancellationToken ct)
        => Task.Run(() => Run(toolCallId, cmd, args, cwdRaw, timeoutRaw, extraEnv, podmanEnv, allowNetwork, workspaceWin, outWin), ct);

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
        string outWin)
    {
        // ── Validate inputs ───────────────────────────────────────────────────
        if (string.IsNullOrWhiteSpace(cmd))
            return Error("cmd is required.");

        if (cmd.IndexOfAny(ControlChars) >= 0)
            return Error("cmd must not contain control characters.");

        if (!PathMapping.TrySanitizeCwd(cwdRaw, out var cwd))
            return Error($"Invalid cwd '{cwdRaw}': must be a relative path with no traversal.");

        var timeout = Math.Clamp(timeoutRaw, 1, 3600);

        // ── Resolve paths ─────────────────────────────────────────────────────
        var containerName = $"wsl-sandbox-mcp-{toolCallId}";
        var wslWorkspace  = PathMapping.ToWslPath(workspaceWin);
        if (string.IsNullOrWhiteSpace(wslWorkspace))
            return Error("Cannot map workspace path to WSL /mnt/... path.");

        var workCwd = cwd == "." ? "/workspace" : $"/workspace/{cwd}";
        var metaRel = $"out/{toolCallId}.meta.json";

        // ── Build podman flags ────────────────────────────────────────────────
        var networkFlag = allowNetwork ? "" : "--network none";
        var envFlags = string.Join(" ",
            extraEnv
                .Where(kv => !string.IsNullOrEmpty(kv.Key) && !kv.Key.Contains('='))
                .Select(kv => $"--env {Q(kv.Key)}={Q(kv.Value)}"));
        var argsStr = string.Join(" ", args.Select(Q));

        var startedTs = DateTimeOffset.UtcNow;

        // ── 1. podman create ──────────────────────────────────────────────────
        var createParts = new[]
        {
            podmanEnv,
            "podman create",
            $"--name {Q(containerName)}",
            "--rm=false",
            networkFlag,
            $"-v {Q(wslWorkspace + ":/workspace:rw")}",
            $"-w {Q(workCwd)}",
            envFlags,
            Q(ImageName),
            Q(cmd),
            argsStr,
        };
        var createScript = string.Join(" ", createParts.Where(p => !string.IsNullOrEmpty(p))).Trim();

        var createResult = ProcessExec.WslSh(createScript, 30);
        if (createResult.exitCode != 0)
        {
            return Error(
                $"podman create failed (exit {createResult.exitCode}): " +
                createResult.stderr.Trim());
        }

        // ── 2. podman start -a (capture output, honour timeout) ───────────────
        var startScript = $"{podmanEnv} podman start -a {Q(containerName)}";
        var startResult = ProcessExec.WslSh(startScript, timeout + 5);

        bool timedOut = startResult.exitCode == -1 &&
                        startResult.stderr.Contains("Timeout", StringComparison.OrdinalIgnoreCase);
        if (timedOut)
        {
            Console.Error.WriteLine($"[LinuxCliRunner] Timeout ({timeout}s) hit for {containerName}; stopping.");
            ProcessExec.WslSh($"{podmanEnv} podman stop -t 1 {Q(containerName)}", 15);
        }

        var finishedTs = DateTimeOffset.UtcNow;

        // ── 3. Write meta JSON ────────────────────────────────────────────────
        string? artifactMeta = null;
        try
        {
            var meta = new JsonObject
            {
                ["image"]       = ImageName,
                ["cmd"]         = cmd,
                ["args"]        = new JsonArray(args.Select(a => (JsonNode)JsonValue.Create(a)!).ToArray()),
                ["cwd"]         = cwd,
                ["env"]         = new JsonObject(
                    extraEnv.Select(kv =>
                        new KeyValuePair<string, JsonNode?>(kv.Key, JsonValue.Create(kv.Value)))),
                ["exit_code"]   = startResult.exitCode,
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

        // ── 4. podman rm ──────────────────────────────────────────────────────
        ProcessExec.WslSh($"{podmanEnv} podman rm {Q(containerName)}", 30);

        return new RunResult(
            startResult.exitCode,
            startResult.stdout,
            startResult.stderr,
            timedOut,
            artifactMeta);
    }

    private static RunResult Error(string message) =>
        new RunResult(-1, "", message, false, null);
}

