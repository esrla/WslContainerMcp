// WslContainerMcp – Windows-only MCP server over stdio (.NET 10)
// All logic lives in this single file.
//
// Behaviour summary:
//   • Exits immediately if not running on Windows.
//   • Always exposes exactly one MCP tool:
//       - run_linux_cli            when WSL + Podman are fully ready.
//       - environment_issue_report when any probe or bootstrap step fails;
//         calling it returns a string describing what went wrong so the user
//         can diagnose and fix the issue without leaving the chat.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

// ──────────────────────────────────────────────────────────────────────────────
// Entry point (top-level statements)
// ──────────────────────────────────────────────────────────────────────────────

if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
{
    Console.Error.WriteLine("[WslContainerMcp] This server requires Windows.");
    return 1;
}

var userProfile    = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
var rootWin        = Path.Combine(userProfile, ".wsl-sandbox-mcp");
var workspaceWin   = Path.Combine(rootWin, "workspace");
var outWin         = Path.Combine(workspaceWin, "out");
var containerDirWin = Path.Combine(rootWin, "container");

try
{
    Directory.CreateDirectory(outWin);
    Directory.CreateDirectory(containerDirWin);

    // Ensure agent Dockerfile exists in the runtime container directory.
    var dfPath = Path.Combine(containerDirWin, "Dockerfile");
    if (!File.Exists(dfPath))
        File.WriteAllText(dfPath, Embedded.Dockerfile, new UTF8Encoding(false));
}
catch (Exception ex)
{
    Console.Error.WriteLine($"[WslContainerMcp] Failed to create workspace dirs: {ex.Message}");
    return 1;
}

// ── WSL probe – capture failure; do NOT exit (always serve a tool) ─────────

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

// ── MCP server loop ────────────────────────────────────────────────────────

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

await McpServer.RunAsync(podmanReady, podmanEnv, issueReport, workspaceWin, outWin, cts.Token);
return 0;

// ══════════════════════════════════════════════════════════════════════════════
// Embedded assets
// ══════════════════════════════════════════════════════════════════════════════

static class Embedded
{
    /// <summary>
    /// Dockerfile for the wsl-sandbox-mcp-agent:latest image.
    /// Includes python3, pip, curl, tar, and bash on top of Ubuntu 24.04.
    /// </summary>
    public const string Dockerfile =
        "FROM ubuntu:24.04\n" +
        "ENV DEBIAN_FRONTEND=noninteractive\n" +
        "RUN apt-get update && \\\n" +
        "    apt-get install -y --no-install-recommends \\\n" +
        "        python3 python3-pip curl ca-certificates tar bash && \\\n" +
        "    rm -rf /var/lib/apt/lists/*\n" +
        "WORKDIR /workspace\n";
}

// ══════════════════════════════════════════════════════════════════════════════
// ProcessExec – spawn processes and run WSL shell commands
// ══════════════════════════════════════════════════════════════════════════════

static class ProcessExec
{
    /// <summary>Shell single-quote escape for use inside a POSIX sh script.</summary>
    public static string Q(string s) => "'" + (s ?? "").Replace("'", "'\"'\"'") + "'";

    /// <summary>
    /// Run a POSIX shell script inside the default WSL distro via
    /// <c>wsl.exe -e sh -lc &lt;script&gt;</c>.
    /// Using <see cref="ProcessStartInfo.ArgumentList"/> avoids Windows
    /// command-line re-parsing so the script is passed verbatim.
    /// </summary>
    public static (int exitCode, string stdout, string stderr) WslSh(string script, int timeoutSeconds)
    {
        var psi = new ProcessStartInfo("wsl.exe")
        {
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            CreateNoWindow         = true,
        };
        psi.ArgumentList.Add("-e");
        psi.ArgumentList.Add("sh");
        psi.ArgumentList.Add("-lc");
        psi.ArgumentList.Add(script);
        return Run(psi, timeoutSeconds);
    }

    /// <summary>Run an arbitrary executable with a pre-built argument string.</summary>
    public static (int exitCode, string stdout, string stderr) Exec(
        string fileName, string arguments, int timeoutSeconds)
    {
        var psi = new ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            CreateNoWindow         = true,
        };
        return Run(psi, timeoutSeconds);
    }

    private static (int exitCode, string stdout, string stderr) Run(
        ProcessStartInfo psi, int timeoutSeconds)
    {
        using var p = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        using var outDone = new ManualResetEventSlim(false);
        using var errDone = new ManualResetEventSlim(false);

        p.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null) outDone.Set();
            else stdout.AppendLine(e.Data);
        };
        p.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) errDone.Set();
            else stderr.AppendLine(e.Data);
        };

        try
        {
            if (!p.Start()) return (-1, "", "Failed to start process.");
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();

            if (!p.WaitForExit(timeoutSeconds * 1000))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* best-effort */ }
                return (-1, stdout.ToString(), (stderr + "\nTimeout").Trim());
            }

            outDone.Wait(2000);
            errDone.Wait(2000);
            return (p.ExitCode, stdout.ToString(), stderr.ToString());
        }
        catch (Exception ex)
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { /* best-effort */ }
            return (-1, stdout.ToString(), (stderr + "\n" + ex.Message).Trim());
        }
    }
}

// ══════════════════════════════════════════════════════════════════════════════
// WslProbe – check whether WSL is usable
// ══════════════════════════════════════════════════════════════════════════════

static class WslProbe
{
    /// <summary>wsl.exe is callable if --version or --status exits 0.</summary>
    public static bool IsWslCallable()
    {
        var r = ProcessExec.Exec("wsl.exe", "--version", 5);
        if (r.exitCode == 0) return true;
        r = ProcessExec.Exec("wsl.exe", "--status", 5);
        return r.exitCode == 0;
    }

    /// <summary>At least one distro is listed by <c>wsl -l -v</c>.</summary>
    public static bool HasAnyDistro()
    {
        var r = ProcessExec.Exec("wsl.exe", "-l -v", 10);
        if (r.exitCode != 0) return false;
        var combined = ((r.stdout ?? "") + "\n" + (r.stderr ?? "")).Trim();
        if (combined.Length == 0) return false;

        foreach (var line in combined.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
        {
            var t = line.Trim();
            if (t.StartsWith("NAME", StringComparison.OrdinalIgnoreCase)) continue;
            if (t.StartsWith("*")) t = t[1..].Trim();
            if (t.Length == 0) continue;
            if (t.Contains("Running", StringComparison.OrdinalIgnoreCase) ||
                t.Contains("Stopped", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary><c>wsl -e sh -lc "echo ok"</c> returns "ok".</summary>
    public static bool CanRunShell()
    {
        var r = ProcessExec.WslSh("echo ok", 10);
        return r.exitCode == 0 && (r.stdout ?? "").Trim() == "ok";
    }
}

// ══════════════════════════════════════════════════════════════════════════════
// PodmanBootstrap – configure Podman storage, install if missing, build image
// ══════════════════════════════════════════════════════════════════════════════

static class PodmanBootstrap
{
    const string ImageName = "wsl-sandbox-mcp-agent:latest";

    static string Q(string s) => ProcessExec.Q(s);

    public static Task<(bool ready, string podmanEnv, string? issueReport)> RunAsync(string containerDirWin)
        => Task.Run(() => Run(containerDirWin));

    private static (bool ready, string podmanEnv, string? issueReport) Run(string containerDirWin)
    {
        try
        {
            // 1. Resolve WSL $HOME
            var home = ProcessExec.WslSh("echo $HOME", 10).stdout.Trim();
            if (string.IsNullOrWhiteSpace(home))
            {
                const string msg = "BOOTSTRAP_HOME_MISSING: Could not resolve $HOME inside WSL. " +
                                   "Ensure your WSL distro has a properly configured user home directory.";
                Console.Error.WriteLine($"[Bootstrap] {msg}");
                return (false, "", msg);
            }

            // 2. Create stable storage directories inside WSL
            var baseDir     = $"{home}/.wsl-sandbox-mcp";
            var storageConf = $"{baseDir}/storage.conf";
            var graphRoot   = $"{baseDir}/podman/graphroot";
            var runRoot     = $"{baseDir}/podman/runroot";

            var mkdirs = ProcessExec.WslSh(
                $"mkdir -p {Q(baseDir)} {Q(graphRoot)} {Q(runRoot)}", 30);
            if (mkdirs.exitCode != 0)
            {
                var msg = $"BOOTSTRAP_MKDIR_FAILED: Could not create required directories inside WSL.\n" +
                          $"Details: {mkdirs.stderr.Trim()}";
                Console.Error.WriteLine($"[Bootstrap] {msg}");
                return (false, "", msg);
            }

            // 3. Write storage.conf
            var confContent =
                "[storage]\n" +
                "driver = \"overlay\"\n" +
                $"graphroot = \"{graphRoot}\"\n" +
                $"runroot = \"{runRoot}\"\n";

            if (!WriteToWsl(storageConf, confContent))
            {
                const string msg = "BOOTSTRAP_STORAGE_CONF_FAILED: Failed to write storage.conf inside WSL. " +
                                   "Ensure python3 or base64 is available in your WSL distro.";
                Console.Error.WriteLine($"[Bootstrap] {msg}");
                return (false, "", msg);
            }

            var podmanEnv = $"CONTAINERS_STORAGE_CONF={Q(storageConf)}";

            // 4. Verify or install Podman
            if (!CommandExistsInWsl("podman"))
            {
                Console.Error.WriteLine("[Bootstrap] podman not found; attempting non-interactive install...");
                if (!TryInstallPodman(out var installMsg))
                {
                    Console.Error.WriteLine($"[Bootstrap] Podman install failed: {installMsg}");
                    return (false, podmanEnv, installMsg);
                }
            }

            if (ProcessExec.WslSh($"{podmanEnv} podman --version", 10).exitCode != 0)
            {
                const string msg = "PODMAN_VERSION_FAILED: 'podman --version' failed inside WSL. " +
                                   "Podman may be installed but not functional. Try running it manually.";
                Console.Error.WriteLine($"[Bootstrap] {msg}");
                return (false, podmanEnv, msg);
            }

            var podmanInfo = ProcessExec.WslSh($"{podmanEnv} podman info", 30);
            if (podmanInfo.exitCode != 0)
            {
                var msg = "PODMAN_INFO_FAILED: 'podman info' failed inside WSL. " +
                          "This often indicates a missing kernel feature (e.g. cgroups v2) or user namespace issue.\n" +
                          $"Details: {podmanInfo.stderr.Trim()}";
                Console.Error.WriteLine($"[Bootstrap] {msg}");
                return (false, podmanEnv, msg);
            }

            // 5. Ensure agent image exists
            if (ProcessExec.WslSh($"{podmanEnv} podman image exists {Q(ImageName)}", 10).exitCode != 0)
            {
                Console.Error.WriteLine($"[Bootstrap] Image {ImageName} missing; building...");
                if (!BuildImage(podmanEnv, containerDirWin, out var buildMsg))
                {
                    Console.Error.WriteLine($"[Bootstrap] Image build failed: {buildMsg}");
                    return (false, podmanEnv, buildMsg);
                }
                if (ProcessExec.WslSh($"{podmanEnv} podman image exists {Q(ImageName)}", 10).exitCode != 0)
                {
                    const string msg = "IMAGE_STILL_MISSING: Image build appeared to succeed but " +
                                       $"'{ImageName}' is still not listed by Podman. Check your Podman storage config.";
                    Console.Error.WriteLine($"[Bootstrap] {msg}");
                    return (false, podmanEnv, msg);
                }
            }

            Console.Error.WriteLine("[Bootstrap] All checks passed.");
            return (true, podmanEnv, null);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Bootstrap] Exception: {ex.Message}");
            var exMsg = $"BOOTSTRAP_EXCEPTION: Unexpected error during bootstrap: {ex.Message}";
            return (false, "", exMsg);
        }
    }

    private static bool CommandExistsInWsl(string name)
        => ProcessExec.WslSh($"command -v {Q(name)} >/dev/null 2>&1", 10).exitCode == 0;

    private static bool TryInstallPodman(out string issueMsg)
    {
        // Only attempt non-interactive install (sudo -n)
        if (ProcessExec.WslSh("sudo -n true", 10).exitCode != 0)
        {
            issueMsg = "SUDO_NOT_NONINTERACTIVE: 'sudo -n true' failed inside WSL. " +
                       "Podman is not installed and it cannot be installed non-interactively without passwordless sudo. " +
                       "Either install Podman manually ('sudo apt-get install -y podman') or configure passwordless sudo.";
            Console.Error.WriteLine($"[Bootstrap] {issueMsg}");
            return false;
        }

        var pm = DetectPackageManager();
        if (string.IsNullOrEmpty(pm))
        {
            issueMsg = "PKG_MANAGER_MISSING: No supported package manager found (apt-get / dnf / apk) inside WSL. " +
                       "Install Podman manually and restart the server.";
            Console.Error.WriteLine($"[Bootstrap] {issueMsg}");
            return false;
        }

        (int exitCode, string stdout, string stderr) result = pm switch
        {
            "apt-get" => InstallWithApt("podman"),
            "dnf"     => ProcessExec.WslSh("sudo -n dnf install -y podman", 900),
            "apk"     => ProcessExec.WslSh("sudo -n apk add podman", 900),
            _         => (exitCode: 1, stdout: "", stderr: "Unknown package manager"),
        };

        if (result.exitCode != 0)
        {
            issueMsg = $"PODMAN_INSTALL_FAILED: Package installation of Podman failed via {pm}.\n" +
                       $"Details: {result.stderr?.Trim()}\n" +
                       "Try installing Podman manually inside WSL and then restart the server.";
            Console.Error.WriteLine($"[Bootstrap] Package install failed: {result.stderr?.Trim()}");
            return false;
        }
        issueMsg = ""; // required by compiler: out parameter must be assigned on all paths
        return true;
    }

    private static (int exitCode, string stdout, string stderr) InstallWithApt(string pkg)
    {
        var update = ProcessExec.WslSh("sudo -n apt-get update -qq", 600);
        if (update.exitCode != 0) return update;
        return ProcessExec.WslSh($"sudo -n apt-get install -y {pkg}", 900);
    }

    private static string DetectPackageManager()
    {
        if (CommandExistsInWsl("apt-get")) return "apt-get";
        if (CommandExistsInWsl("dnf"))     return "dnf";
        if (CommandExistsInWsl("apk"))     return "apk";
        return "";
    }

    private static bool BuildImage(string podmanEnv, string containerDirWin, out string issueMsg)
    {
        var wslPath = PathMapping.ToWslPath(containerDirWin);
        if (string.IsNullOrWhiteSpace(wslPath))
        {
            issueMsg = "IMAGE_PATH_FAILED: Cannot map the container directory to a WSL /mnt/... path. " +
                       "Ensure the workspace is on a drive letter path (e.g. C:\\...).";
            Console.Error.WriteLine($"[Bootstrap] {issueMsg}");
            return false;
        }
        var r = ProcessExec.WslSh($"{podmanEnv} podman build -t {Q(ImageName)} {Q(wslPath)}", 900);
        if (r.exitCode != 0)
        {
            issueMsg = $"IMAGE_BUILD_FAILED: 'podman build' for {ImageName} failed.\n" +
                       $"Details: {r.stderr?.Trim()}\n" +
                       $"Check the Dockerfile at: {containerDirWin}";
            Console.Error.WriteLine($"[Bootstrap] Build error: {r.stderr?.Trim()}");
            return false;
        }
        issueMsg = ""; // required by compiler: out parameter must be assigned on all paths
        return true;
    }

    /// <summary>
    /// Write UTF-8 content to a WSL path by base64-encoding it so that no
    /// shell-special characters in the content can cause issues.
    /// </summary>
    private static bool WriteToWsl(string wslPath, string content)
    {
        var b64    = Convert.ToBase64String(Encoding.UTF8.GetBytes(content));
        var script = $"printf '%s' {Q(b64)} | base64 -d > {Q(wslPath)}";
        return ProcessExec.WslSh(script, 30).exitCode == 0;
    }
}

// ══════════════════════════════════════════════════════════════════════════════
// PathMapping – Windows ↔ WSL path conversion and cwd sanitization
// ══════════════════════════════════════════════════════════════════════════════

static class PathMapping
{
    /// <summary>
    /// Convert a Windows absolute path to its /mnt/<drive>/... WSL equivalent.
    /// Returns null if the path cannot be mapped.
    /// </summary>
    public static string? ToWslPath(string winPath)
    {
        if (string.IsNullOrWhiteSpace(winPath)) return null;
        winPath = Path.GetFullPath(winPath);
        var root = Path.GetPathRoot(winPath);
        if (string.IsNullOrWhiteSpace(root) || root.Length < 2 || root[1] != ':') return null;
        var drive = char.ToLowerInvariant(root[0]);
        var rest  = winPath[2..].Replace('\\', '/');
        if (rest.StartsWith('/')) rest = rest[1..];
        return $"/mnt/{drive}/{rest}";
    }

    /// <summary>
    /// Validate and normalise a user-supplied cwd.
    /// Returns false and leaves <paramref name="sanitized"/> as "." when the
    /// path is rejected (absolute, traversal attempt, or contains colons).
    /// </summary>
    public static bool TrySanitizeCwd(string? cwd, out string sanitized)
    {
        sanitized = ".";
        if (string.IsNullOrWhiteSpace(cwd) || cwd.Trim() == ".") return true;

        // Reject absolute paths (Unix-style or Windows-style)
        if (Path.IsPathRooted(cwd)) return false;

        // Reject drive prefixes (e.g. C:)
        if (cwd.Contains(':')) return false;

        // Reject traversal segments
        var segments = cwd.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(s => s == "..")) return false;

        // Normalise to forward slashes
        sanitized = string.Join("/", segments);
        return true;
    }
}

// ══════════════════════════════════════════════════════════════════════════════
// McpServer – JSON-RPC 2.0 / MCP message loop over stdio
// ══════════════════════════════════════════════════════════════════════════════

static class McpServer
{
    const string ServerName      = "WslContainerMcp";
    const string ServerVersion   = "0.1.0";
    const string ProtocolVersion = "2024-11-05";

    static readonly JsonSerializerOptions CompactJson = new() { WriteIndented = false };

    public static async Task RunAsync(
        bool      podmanReady,
        string    podmanEnv,
        string?   issueReport,
        string    workspaceWin,
        string    outWin,
        CancellationToken ct)
    {
        using var reader = new StreamReader(Console.OpenStandardInput(),  new UTF8Encoding(false));
        using var writer = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false))
            { AutoFlush = true };

        Console.Error.WriteLine(
            podmanReady
                ? "[McpServer] Ready (tool: run_linux_cli)"
                : "[McpServer] Ready (tool: environment_issue_report)");

        while (!ct.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[McpServer] Read error: {ex.Message}");
                break;
            }

            if (line == null) break;          // EOF
            line = line.Trim();
            if (line.Length == 0) continue;

            JsonNode? msg;
            try   { msg = JsonNode.Parse(line); }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[McpServer] JSON parse error: {ex.Message}");
                await WriteError(writer, null, -32700, "Parse error").ConfigureAwait(false);
                continue;
            }
            if (msg == null) continue;

            var id     = msg["id"];
            var method = msg["method"]?.GetValue<string>();

            if (string.IsNullOrWhiteSpace(method)) continue; // ignore unknown/empty

            // Notifications have no "id" – do not respond.
            if (id == null) continue;

            Console.Error.WriteLine($"[McpServer] → {method}  id={id}");

            try
            {
                switch (method)
                {
                    case "initialize":
                        await HandleInitialize(writer, id).ConfigureAwait(false);
                        break;

                    case "tools/list":
                        await HandleToolsList(writer, id, podmanReady, issueReport).ConfigureAwait(false);
                        break;

                    case "tools/call":
                        await HandleToolsCall(
                            writer, id, msg["params"],
                            podmanReady, podmanEnv, issueReport, workspaceWin, outWin, ct)
                            .ConfigureAwait(false);
                        break;

                    case "ping":
                        await WriteResult(writer, id, new JsonObject()).ConfigureAwait(false);
                        break;

                    default:
                        await WriteError(writer, id, -32601, $"Method not found: {method}")
                            .ConfigureAwait(false);
                        break;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[McpServer] Handler error for {method}: {ex}");
                await WriteError(writer, id, -32603, ex.Message).ConfigureAwait(false);
            }
        }

        Console.Error.WriteLine("[McpServer] Shutting down.");
    }

    // ── Handlers ──────────────────────────────────────────────────────────────

    static Task HandleInitialize(StreamWriter w, JsonNode id) =>
        WriteResult(w, id, new JsonObject
        {
            ["protocolVersion"] = ProtocolVersion,
            ["capabilities"]    = new JsonObject { ["tools"] = new JsonObject() },
            ["serverInfo"]      = new JsonObject
            {
                ["name"]    = ServerName,
                ["version"] = ServerVersion,
            },
        });

    static Task HandleToolsList(StreamWriter w, JsonNode id, bool podmanReady, string? issueReport)
    {
        var tools = new JsonArray();
        if (podmanReady)
            tools.Add(BuildRunLinuxCliDefinition());
        else
            tools.Add(BuildEnvironmentIssueReportDefinition());
        return WriteResult(w, id, new JsonObject { ["tools"] = tools });
    }

    static async Task HandleToolsCall(
        StreamWriter      w,
        JsonNode          id,
        JsonNode?         @params,
        bool              podmanReady,
        string            podmanEnv,
        string?           issueReport,
        string            workspaceWin,
        string            outWin,
        CancellationToken ct)
    {
        var toolName = @params?["name"]?.GetValue<string>();

        if (toolName == "environment_issue_report")
        {
            var report = issueReport ?? "No issue detected; the environment appears to be healthy.";
            await WriteResult(w, id, new JsonObject
            {
                ["content"] = new JsonArray
                {
                    new JsonObject { ["type"] = "text", ["text"] = report }
                },
                ["isError"] = false,
            }).ConfigureAwait(false);
            return;
        }

        if (toolName != "run_linux_cli")
        {
            await WriteError(w, id, -32601, $"Tool not found: {toolName}").ConfigureAwait(false);
            return;
        }

        if (!podmanReady)
        {
            // run_linux_cli was explicitly called but environment is not ready.
            var report = issueReport ?? "Podman is not ready.";
            await WriteResult(w, id, new JsonObject
            {
                ["content"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "text",
                        ["text"] = $"Tool run_linux_cli is not available.\n{report}",
                    }
                },
                ["isError"] = true,
            }).ConfigureAwait(false);
            return;
        }

        var toolCallId = SanitizeId(id.ToString());
        var arguments  = @params?["arguments"];

        var result = await LinuxCliRunner.RunAsync(
            toolCallId, arguments, podmanEnv, workspaceWin, outWin, ct)
            .ConfigureAwait(false);

        var resultJson = new JsonObject
        {
            ["exit_code"] = result.ExitCode,
            ["stdout"]    = result.Stdout,
            ["stderr"]    = result.Stderr,
            ["content"]   = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = BuildTextSummary(result),
                }
            },
            ["isError"] = result.ExitCode != 0,
        };

        if (result.ArtifactTar  != null) resultJson["artifact_tar"]  = result.ArtifactTar;
        if (result.ArtifactMeta != null) resultJson["artifact_meta"] = result.ArtifactMeta;

        await WriteResult(w, id, resultJson).ConfigureAwait(false);
    }

    // ── Tool definitions ──────────────────────────────────────────────────────

    static JsonObject BuildRunLinuxCliDefinition() => new()
    {
        ["name"]        = "run_linux_cli",
        ["description"] = "Run a command inside a disposable Podman Linux container " +
                          "(wsl-sandbox-mcp-agent:latest). " +
                          "Returns stdout, stderr, exit_code, and artifact paths for " +
                          "the exported container filesystem tar and a metadata JSON.",
        ["inputSchema"] = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["cmd"]  = new JsonObject
                {
                    ["type"]        = "string",
                    ["description"] = "Executable to run inside the container",
                },
                ["args"] = new JsonObject
                {
                    ["type"]        = "array",
                    ["items"]       = new JsonObject { ["type"] = "string" },
                    ["description"] = "Arguments to pass to the executable",
                },
                ["cwd"] = new JsonObject
                {
                    ["type"]        = "string",
                    ["description"] = "Working directory relative to workspace root (default: '.')",
                },
                ["timeout_s"] = new JsonObject
                {
                    ["type"]        = "integer",
                    ["description"] = "Timeout in seconds, clamped to 1..3600 (default: 120)",
                },
                ["env"] = new JsonObject
                {
                    ["type"]                 = "object",
                    ["additionalProperties"] = new JsonObject { ["type"] = "string" },
                    ["description"]          = "Extra environment variables for the container",
                },
            },
            ["required"] = new JsonArray { "cmd", "args" },
        },
    };

    /// <summary>
    /// Exposed in place of run_linux_cli when the environment is not ready.
    /// Calling it returns a human-readable description of the failure so the
    /// user can diagnose and fix the problem without leaving their AI chat.
    /// </summary>
    static JsonObject BuildEnvironmentIssueReportDefinition() => new()
    {
        ["name"]        = "environment_issue_report",
        ["description"] = "Returns a description of why the Linux container environment " +
                          "is not available. Call this to find out what needs to be fixed " +
                          "(e.g. WSL not installed, Podman missing, image build failed).",
        ["inputSchema"] = new JsonObject
        {
            ["type"]       = "object",
            ["properties"] = new JsonObject(),   // no inputs
        },
    };

    // ── Helpers ───────────────────────────────────────────────────────────────

    static string BuildTextSummary(LinuxCliRunner.RunResult r)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"exit_code: {r.ExitCode}");
        if (r.TimedOut) sb.AppendLine("(command timed out)");
        if (r.ArtifactTar  != null) sb.AppendLine($"artifact_tar:  {r.ArtifactTar}");
        if (r.ArtifactMeta != null) sb.AppendLine($"artifact_meta: {r.ArtifactMeta}");
        sb.AppendLine("--- stdout ---");
        sb.AppendLine(r.Stdout.TrimEnd());
        sb.AppendLine("--- stderr ---");
        sb.AppendLine(r.Stderr.TrimEnd());
        return sb.ToString();
    }

    static string SanitizeId(string id)
    {
        var sb = new StringBuilder(id.Length);
        foreach (var c in id)
            sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
        return sb.Length > 0 ? sb.ToString() : "id";
    }

    static async Task WriteResult(StreamWriter w, JsonNode? id, JsonNode result)
    {
        var obj = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"]      = id?.DeepClone(),
            ["result"]  = result,
        };
        await w.WriteLineAsync(obj.ToJsonString(CompactJson)).ConfigureAwait(false);
    }

    static async Task WriteError(StreamWriter w, JsonNode? id, int code, string message)
    {
        var obj = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"]      = id?.DeepClone(),
            ["error"]   = new JsonObject
            {
                ["code"]    = code,
                ["message"] = message,
            },
        };
        await w.WriteLineAsync(obj.ToJsonString(CompactJson)).ConfigureAwait(false);
    }
}

// ══════════════════════════════════════════════════════════════════════════════
// LinuxCliRunner – implements the run_linux_cli MCP tool
// ══════════════════════════════════════════════════════════════════════════════

static class LinuxCliRunner
{
    const string ImageName = "wsl-sandbox-mcp-agent:latest";

    static readonly char[] ControlChars = { '\n', '\r', '\0' };

    static string Q(string s) => ProcessExec.Q(s);

    public record RunResult(
        int     ExitCode,
        string  Stdout,
        string  Stderr,
        bool    TimedOut,
        string? ArtifactTar,
        string? ArtifactMeta);

    public static Task<RunResult> RunAsync(
        string            toolCallId,
        JsonNode?         arguments,
        string            podmanEnv,
        string            workspaceWin,
        string            outWin,
        CancellationToken ct)
        => Task.Run(() => Run(toolCallId, arguments, podmanEnv, workspaceWin, outWin), ct);

    // ── Core implementation ───────────────────────────────────────────────────

    private static RunResult Run(
        string    toolCallId,
        JsonNode? arguments,
        string    podmanEnv,
        string    workspaceWin,
        string    outWin)
    {
        // ── Parse inputs ──────────────────────────────────────────────────────
        var cmd = arguments?["cmd"]?.GetValue<string>() ?? "";
        if (string.IsNullOrWhiteSpace(cmd))
            return Error("cmd is required.");

        if (cmd.IndexOfAny(ControlChars) >= 0)
            return Error("cmd must not contain control characters.");

        var argsArr  = (arguments?["args"] as JsonArray)
                       ?.Select(n => n?.GetValue<string>() ?? "")
                       .ToArray() ?? Array.Empty<string>();

        var cwdRaw = arguments?["cwd"]?.GetValue<string>();
        if (!PathMapping.TrySanitizeCwd(cwdRaw, out var cwd))
            return Error($"Invalid cwd '{cwdRaw}': must be a relative path with no traversal.");

        var timeoutRaw = 120;
        if (arguments?["timeout_s"] is JsonNode tsNode)
        {
            try { timeoutRaw = tsNode.GetValue<int>(); } catch { /* ignore – use default */ }
        }
        var timeout = Math.Clamp(timeoutRaw, 1, 3600);

        var extraEnv = new Dictionary<string, string>(StringComparer.Ordinal);
        if (arguments?["env"] is JsonObject envObj)
        {
            foreach (var kv in envObj)
            {
                if (kv.Value != null)
                    extraEnv[kv.Key] = kv.Value.GetValue<string>();
            }
        }

        // ── Resolve paths ─────────────────────────────────────────────────────
        var containerName = $"wsl-sandbox-mcp-{toolCallId}";
        var wslWorkspace  = PathMapping.ToWslPath(workspaceWin);
        if (string.IsNullOrWhiteSpace(wslWorkspace))
            return Error("Cannot map workspace path to WSL /mnt/... path.");

        var workCwd    = cwd == "." ? "/workspace" : $"/workspace/{cwd}";
        var wslTarPath = $"{wslWorkspace}/out/{toolCallId}.tar";
        var tarRel     = $"out/{toolCallId}.tar";
        var metaRel    = $"out/{toolCallId}.meta.json";

        // ── Build podman flags ────────────────────────────────────────────────
        // Quote key and value separately so an '=' in the value cannot
        // break the intended key=value structure passed to --env.
        var envFlags = string.Join(" ",
            extraEnv
                .Where(kv => !string.IsNullOrEmpty(kv.Key) && !kv.Key.Contains('='))
                .Select(kv => $"--env {Q(kv.Key)}={Q(kv.Value)}"));
        var argsStr = string.Join(" ", argsArr.Select(Q));

        var startedTs = DateTimeOffset.UtcNow;

        // ── 1. podman create ──────────────────────────────────────────────────
        var createScript = string.Join(" ",
            podmanEnv,
            "podman create",
            $"--name {Q(containerName)}",
            "--rm=false",
            $"-v {Q(wslWorkspace + ":/workspace:rw")}",
            $"-w {Q(workCwd)}",
            envFlags,
            Q(ImageName),
            Q(cmd),
            argsStr).Trim();

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

        // ── 3. podman export (best-effort) ────────────────────────────────────
        var exportResult = ProcessExec.WslSh(
            $"{podmanEnv} podman export {Q(containerName)} -o {Q(wslTarPath)}", 120);
        string? artifactTar = exportResult.exitCode == 0 ? tarRel : null;

        // ── 4. Write meta JSON ────────────────────────────────────────────────
        string? artifactMeta = null;
        try
        {
            var meta = new JsonObject
            {
                ["image"]       = ImageName,
                ["cmd"]         = cmd,
                ["args"]        = new JsonArray(argsArr.Select(a => (JsonNode)JsonValue.Create(a)!).ToArray()),
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
                new UTF8Encoding(false));
            artifactMeta = metaRel;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[LinuxCliRunner] meta write failed: {ex.Message}");
        }

        // ── 5. podman rm ──────────────────────────────────────────────────────
        ProcessExec.WslSh($"{podmanEnv} podman rm {Q(containerName)}", 30);

        return new RunResult(
            startResult.exitCode,
            startResult.stdout,
            startResult.stderr,
            timedOut,
            artifactTar,
            artifactMeta);
    }

    static RunResult Error(string message) =>
        new RunResult(-1, "", message, false, null, null);
}
