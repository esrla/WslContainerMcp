Prompt: Build “Wsl sandbox mcp” (C# MCP server over stdio; WSL-gated; exit or 1 tool; inspectable container state)

Goal
Create a Windows-only MCP server executable named Wsl container mcp implemented in C#/.NET that communicates via stdio (stdin/stdout). The server must expose no tools unless Windows Subsystem for Linux (WSL) is available and usable. In the “WSL usable” case, the server must ensure a Podman-based Linux container execution environment inside WSL and expose exactly one tool: run_linux_cli.

Hard rules
1) If WSL is not usable → MCP server exits.
2) If WSL is usable ensure Podman/container environment is ready → MCP server reports exactly one tool: run_linux_cli.

WSL usability definition (must be implemented)
WSL is “usable” only if:
- wsl.exe is callable (wsl --version when available).

Inspectable container state (must be implemented)
Support BOTH inspection modes:

Mode A: Stable Podman storage path inside WSL (inspect via \wsl$)
- Configure Podman to use a stable per-user storage directory inside WSL:
  - WSL path: /home/<linuxuser>/.wsl-sandbox-mcp/podman
- Implement by generating a storage.conf file and forcing Podman to use it for all invocations:
  - Set CONTAINERS_STORAGE_CONF=/home/<linuxuser>/.wsl-sandbox-mcp/storage.conf
  - In storage.conf, set:
    - graphroot = "/home/<linuxuser>/.wsl-sandbox-mcp/podman/graphroot"
    - runroot  = "/home/<linuxuser>/.wsl-sandbox-mcp/podman/runroot"
- Ensure the MCP server applies this env var to every Podman call (bootstrap + tool execution).
- README must document how to inspect from Windows using \wsl$\<DistroName>\home\<linuxuser>\.wsl-sandbox-mcp\podman.

Mode B: Export full container filesystem after each run (artifact)
- For each run_linux_cli call:
  - Use deterministic container name: wsl-sandbox-mcp-<tool_call_id>
  - After command finishes (success or failure), export filesystem:
    - podman export <name> -o /workspace/out/<tool_call_id>.tar
  - Write manifest:
    - /workspace/out/<tool_call_id>.meta.json containing { image, cmd, args, cwd, env, exit_code, started_ts, finished_ts }
  - Always remove container after export: podman rm <name>
- Keep tar + meta in workspace for inspection.

Podman/container readiness definition (must be implemented)
When WSL is usable:
- Podman is available inside WSL (verify: podman --version executed inside WSL).
- A fixed image name exists: wsl-sandbox-mcp-agent:latest.
- Image exists or can be built non-interactively from a Dockerfile shipped with the project.
If any step fails → report zero tools.

Bootstrap strategy (must be non-interactive)
- Verify-only first:
  - wsl -e sh -lc "command -v podman"
  - wsl -e sh -lc "podman info"
  - wsl -e sh -lc "podman image exists wsl-sandbox-mcp-agent:latest"
- If Podman missing:
  - Attempt install only if it can be done non-interactively:
    - Use sudo -n and fail fast if it requires a password.
    - Example: sudo -n apt-get update && sudo -n apt-get install -y podman
  - If install fails → not ready → zero tools.
- If image missing:
  - Build from shipped Dockerfile:
    - podman build -t wsl-sandbox-mcp-agent:latest <path>
  - If build fails → not ready → zero tools.

Tool: run_linux_cli
Expose exactly one MCP tool.

Name
run_linux_cli

Input schema (JSON)
- cmd (string, required)
- args (string[], required)
- cwd (string, optional; default ".") relative to workspace root
- timeout_s (int, optional; default 120; clamp 1..3600)
- env (object<string,string>, optional)

Output schema (JSON)
- exit_code (int)
- stdout (string)
- stderr (string)
- artifact_tar (string, optional): relative path to exported filesystem tar, e.g. out/<tool_call_id>.tar
- artifact_meta (string, optional): relative path to meta json, e.g. out/<tool_call_id>.meta.json

Workspace rules (per-user, no cross-user mixing)
- Workspace root on Windows: %USERPROFILE%\.wsl-sandbox-mcp\workspace
- Ensure folders exist:
  - %USERPROFILE%\.wsl-sandbox-mcp\workspace\out
- Map workspace into container at /workspace.
- Reject cwd that attempts traversal (.., absolute paths, drive prefixes, or contains :). 

Container invocation (must support export)
Prefer podman create + podman start -a so export can always happen:
- podman create --name <name> --rm=false -v "<wslPathToWorkspace>:/workspace:rw" -w "/workspace/<cwd>" <image> <cmd> <args...>
- podman start -a <name> (capture stdout/stderr + exit code)
- podman export <name> -o /workspace/out/<tool_call_id>.tar
- podman rm <name>

Timeout handling:
- If timeout hits, stop the container (podman stop -t 1 <name>), attempt export, then remove.

MCP protocol over stdio (implementation requirements)
- Implement a minimal MCP server over stdio:
  - Read JSON messages from stdin.
  - Write JSON responses/events to stdout.
  - Single-threaded message loop with cancellation and safe shutdown.
- Tool list computed at startup and cached for the process lifetime.
- If run_linux_cli called when tool not available, return a standard tool-not-available error.

Project structure
Create a .NET solution:
- WslSandboxMcp.sln
- src/WslSandboxMcp/ (console app)
  - WslSandboxMcp.csproj targeting net8.0 (or net8.0-windows)
  - Program.cs
  - Mcp/
    - StdioTransport.cs
    - McpServer.cs
    - JsonRpcModels.cs
  - Runtime/
    - WslProbe.cs (WSL probes)
    - PodmanBootstrap.cs (includes Mode A env + config generation, and image verify/build)
    - LinuxCliRunner.cs (implements run_linux_cli + Mode B export)
    - PathMapping.cs (Windows ↔ WSL path mapping + cwd sanitization)
    - ProcessExec.cs (spawn processes, timeout, capture stdout/stderr)
  - Container/
    - Dockerfile for wsl-sandbox-mcp-agent:latest (include python3 + pip; minimal)
- README.txt must include:
  - How gating works (0 tools vs 1 tool)
  - Mode A inspection path via \wsl$
  - Mode B tar/meta artifact locations under workspace\out
  - Troubleshooting for missing WSL/distro/podman/sudo -n failures

Security and correctness constraints
- Only execute host commands: wsl.exe plus filesystem operations to create workspace folders.
- Validate/sanitize all inputs (cmd, args, cwd, env).
- Quote/escape safely when constructing sh -lc commands.
- Enforce timeouts and terminate best-effort.
- Tool results must return captured stdout/stderr exactly; no added commentary.

Deliverables
- Full compilable C# project as described.
- Dockerfile for the agent image.
- README.txt with inspection instructions for Mode A and Mode B.



Example of Program.exe that exits if wsl is not available:

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

public class Program
{
    public static async Task Main(string[] args)
    {
        try
            {
             EnsureEnvironment()
            }
        catch ...

        var builder = Host.CreateApplicationBuilder(args);
        builder.Logging.AddConsole(consoleLogOptions =>
        {
            // Configure all logs to go to stderr
            consoleLogOptions.LogToStandardErrorThreshold = Microsoft.Extensions.Logging.LogLevel.Trace;
        });

        //var runtimeTools = await GraphServer.ToolsFromClass.GetToolsAsync().ConfigureAwait(false);

        builder.Services
            .AddMcpServer()
            .WithStdioServerTransport()
            .WithToolsFromAssembly();
            //.WithTools(runtimeTools);

        await builder.Build().RunAsync();
    }
}


Use code below for inspiration for ensuring 


using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

public static class WslSandboxEnvironment
{
    public sealed class EnvironmentException : Exception
    {
        public string Code { get; }
        public EnvironmentException(string code, string message) : base(message) => Code = code;
    }

    public static void EnsureEnvironment()
    {
        var rootWin = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".wsl-sandbox-mcp");
        var workspaceWin = Path.Combine(rootWin, "workspace");
        var outWin = Path.Combine(workspaceWin, "out");
        var containerDirWin = Path.Combine(rootWin, "container");
        Directory.CreateDirectory(outWin);
        Directory.CreateDirectory(containerDirWin);

        Require(IsWslCallable(), "WSL_NOT_AVAILABLE", "wsl.exe is not callable (wsl --version/--status failed).");
        Require(HasAnyDistro(), "WSL_NO_DISTRO", "No WSL distro found (wsl -l -v).");
        Require(CanRunShell(), "WSL_SHELL_NOT_WORKING", "WSL shell execution failed (wsl -e sh -lc \"echo ok\").");

        var home = WslSh("echo $HOME", 10).stdout.Trim();
        Require(!string.IsNullOrWhiteSpace(home), "WSL_HOME_MISSING", "Unable to resolve $HOME inside WSL.");

        var baseDir = $"{home}/.wsl-sandbox-mcp";
        var storageConf = $"{baseDir}/storage.conf";
        var graphRoot = $"{baseDir}/podman/graphroot";
        var runRoot = $"{baseDir}/podman/runroot";

        Require(WslSh($"mkdir -p {Q(baseDir)} {Q(graphRoot)} {Q(runRoot)}", 10).exitCode == 0, "WSL_DIR_CREATE_FAILED", "Failed to create required directories inside WSL.");

        var storageConfContent =
            "[storage]\n" +
            "driver = \"overlay\"\n" +
            $"graphroot = \"{graphRoot}\"\n" +
            $"runroot = \"{runRoot}\"\n";

        Require(WriteFileInWsl(storageConf, storageConfContent), "WSL_WRITE_STORAGE_CONF_FAILED", "Failed to write storage.conf inside WSL.");

        var podmanEnv = $"CONTAINERS_STORAGE_CONF={Q(storageConf)}";

        EnsureAllLinuxDepsNonInteractive(podmanEnv);

        Require(WslSh($"{podmanEnv} podman info", 30).exitCode == 0, "PODMAN_INFO_FAILED", "podman info failed inside WSL.");

        var imageName = "wsl-sandbox-mcp-agent:latest";
        if (WslSh($"{podmanEnv} podman image exists {Q(imageName)}", 10).exitCode != 0)
        {
            var dockerfile = Path.Combine(containerDirWin, "Dockerfile");
            Require(File.Exists(dockerfile), "IMAGE_DOCKERFILE_MISSING", $"Dockerfile missing at: {dockerfile}");

            var containerDirWsl = ToWslPath(containerDirWin);
            Require(!string.IsNullOrWhiteSpace(containerDirWsl), "WINPATH_TO_WSLPATH_FAILED", "Failed to map Windows path to WSL /mnt/<drive>/ path.");

            var build = WslSh($"{podmanEnv} podman build -t {Q(imageName)} {Q(containerDirWsl)}", 900);
            Require(build.exitCode == 0, "IMAGE_BUILD_FAILED", $"podman build failed.\n{TrimForUi(build.stderr, 8000)}");

            Require(WslSh($"{podmanEnv} podman image exists {Q(imageName)}", 10).exitCode == 0, "IMAGE_STILL_MISSING", "Image still missing after build.");
        }
    }

    private static void EnsureAllLinuxDepsNonInteractive(string podmanEnv)
    {
        Require(CommandExistsInWsl("sudo"), "SUDO_MISSING", "sudo is not installed inside the WSL distro.");
        Require(WslSh("sudo -n true", 10).exitCode == 0, "SUDO_NOT_NONINTERACTIVE", "sudo requires a password or is not permitted (sudo -n true failed).");

        var pm = DetectPkgManager();
        Require(pm != PkgManager.None, "PKG_MANAGER_MISSING", "No supported package manager found (apt-get/dnf/apk).");

        EnsurePackages(pm, "ca-certificates", "curl");
        if (!CommandExistsInWsl("python3")) EnsurePackages(pm, "python3");
        if (!CommandExistsInWsl("pip3")) EnsurePackages(pm, "python3-pip");
        if (!CommandExistsInWsl("tar")) EnsurePackages(pm, "tar");
        if (!CommandExistsInWsl("podman")) EnsurePackages(pm, "podman");

        Require(WslSh($"{podmanEnv} podman --version", 10).exitCode == 0, "PODMAN_NOT_WORKING", "podman --version failed even after installation.");
    }

    private enum PkgManager { None, Apt, Dnf, Apk }

    private static PkgManager DetectPkgManager()
    {
        if (CommandExistsInWsl("apt-get")) return PkgManager.Apt;
        if (CommandExistsInWsl("dnf")) return PkgManager.Dnf;
        if (CommandExistsInWsl("apk")) return PkgManager.Apk;
        return PkgManager.None;
    }

    private static void EnsurePackages(PkgManager pm, params string[] pkgs)
    {
        if (pkgs == null || pkgs.Length == 0) return;

        if (pm == PkgManager.Apt)
        {
            var u = WslSh("sudo -n apt-get update", 600);
            Require(u.exitCode == 0, "APT_UPDATE_FAILED", $"apt-get update failed.\n{TrimForUi(u.stderr, 8000)}");

            var i = WslSh($"sudo -n apt-get install -y {string.Join(" ", pkgs)}", 900);
            Require(i.exitCode == 0, "APT_INSTALL_FAILED", $"apt-get install failed: {string.Join(" ", pkgs)}\n{TrimForUi(i.stderr, 8000)}");
            return;
        }

        if (pm == PkgManager.Dnf)
        {
            var i = WslSh($"sudo -n dnf install -y {string.Join(" ", pkgs)}", 900);
            Require(i.exitCode == 0, "DNF_INSTALL_FAILED", $"dnf install failed: {string.Join(" ", pkgs)}\n{TrimForUi(i.stderr, 8000)}");
            return;
        }

        if (pm == PkgManager.Apk)
        {
            var i = WslSh($"sudo -n apk add {string.Join(" ", pkgs)}", 900);
            Require(i.exitCode == 0, "APK_INSTALL_FAILED", $"apk add failed: {string.Join(" ", pkgs)}\n{TrimForUi(i.stderr, 8000)}");
            return;
        }

        throw new EnvironmentException("PKG_MANAGER_UNSUPPORTED", "Package manager is unsupported.");
    }

    private static void Require(bool ok, string code, string message)
    {
        if (!ok) throw new EnvironmentException(code, message);
    }

    private static bool IsWslCallable()
    {
        var r = Exec("wsl.exe", "--version", 5);
        if (r.exitCode == 0) return true;
        r = Exec("wsl.exe", "--status", 5);
        return r.exitCode == 0;
    }

    private static bool HasAnyDistro()
    {
        var r = Exec("wsl.exe", "-l -v", 10);
        if (r.exitCode != 0) return false;

        var s = ((r.stdout ?? "") + "\n" + (r.stderr ?? "")).Trim();
        if (s.Length == 0) return false;

        var lines = s.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        var count = 0;
        foreach (var line in lines)
        {
            var t = line.Trim();
            if (t.StartsWith("NAME", StringComparison.OrdinalIgnoreCase)) continue;
            if (t.StartsWith("*")) t = t.Substring(1).Trim();
            if (t.Length == 0) continue;
            if (t.Contains("Running", StringComparison.OrdinalIgnoreCase) || t.Contains("Stopped", StringComparison.OrdinalIgnoreCase)) count++;
        }
        return count > 0;
    }

    private static bool CanRunShell()
    {
        var r = Exec("wsl.exe", "-e sh -lc \"echo ok\"", 10);
        return r.exitCode == 0 && (r.stdout ?? "").Trim() == "ok";
    }

    private static bool CommandExistsInWsl(string name)
    {
        var r = WslSh($"command -v {Q(name)} >/dev/null 2>&1", 10);
        return r.exitCode == 0;
    }

    private static bool WriteFileInWsl(string path, string content)
    {
        var script =
            "python3 - <<'PY'\n" +
            "import os\n" +
            $"p={PyQ(path)}\n" +
            "os.makedirs(os.path.dirname(p), exist_ok=True)\n" +
            $"open(p,'w',encoding='utf-8').write({PyQ(content)})\n" +
            "print('ok')\n" +
            "PY";
        var r = WslSh(script, 30);
        return r.exitCode == 0 && (r.stdout ?? "").Trim().EndsWith("ok", StringComparison.OrdinalIgnoreCase);
    }

    private static (int exitCode, string stdout, string stderr) WslSh(string script, int timeoutSeconds)
    {
        var payload = script.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("$", "\\$");
        var args = $"-e sh -lc \"{payload}\"";
        var r = Exec("wsl.exe", args, timeoutSeconds);
        return (r.exitCode, r.stdout ?? "", r.stderr ?? "");
    }

    private static string Q(string s) => "'" + (s ?? "").Replace("'", "'\"'\"'") + "'";
    private static string PyQ(string s) => "r'''"+ (s ?? "").Replace("'''", "''\\''") + "'''";

    private static string ToWslPath(string winPath)
    {
        if (string.IsNullOrWhiteSpace(winPath)) return null;
        winPath = Path.GetFullPath(winPath);
        var root = Path.GetPathRoot(winPath);
        if (string.IsNullOrWhiteSpace(root) || root.Length < 2 || root[1] != ':') return null;
        var drive = char.ToLowerInvariant(root[0]);
        var rest = winPath.Substring(2).Replace('\\', '/');
        if (rest.StartsWith("/")) rest = rest.Substring(1);
        return $"/mnt/{drive}/{rest}";
    }

    private static string TrimForUi(string s, int max)
    {
        s ??= "";
        s = s.Trim();
        if (s.Length <= max) return s;
        return s.Substring(0, max) + "\n...";
    }

    private static (int exitCode, string stdout, string stderr) Exec(string fileName, string arguments, int timeoutSeconds)
    {
        using var p = new Process();
        p.StartInfo.FileName = fileName;
        p.StartInfo.Arguments = arguments;
        p.StartInfo.UseShellExecute = false;
        p.StartInfo.RedirectStandardOutput = true;
        p.StartInfo.RedirectStandardError = true;
        p.StartInfo.CreateNoWindow = true;

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        using var stdoutDone = new ManualResetEvent(false);
        using var stderrDone = new ManualResetEvent(false);

        p.OutputDataReceived += (_, e) => { if (e.Data == null) stdoutDone.Set(); else stdout.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data == null) stderrDone.Set(); else stderr.AppendLine(e.Data); };

        try
        {
            if (!p.Start()) return (-1, "", "Failed to start process.");
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();

            if (!p.WaitForExit(timeoutSeconds * 1000))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return (-1, stdout.ToString(), (stderr.ToString() + "\nTimeout").Trim());
            }

            stdoutDone.WaitOne(TimeSpan.FromSeconds(2));
            stderrDone.WaitOne(TimeSpan.FromSeconds(2));
            return (p.ExitCode, stdout.ToString(), stderr.ToString());
        }
        catch (Exception ex)
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
            return (-1, stdout.ToString(), (stderr.ToString() + "\n" + ex.Message).Trim());
        }
    }
}




