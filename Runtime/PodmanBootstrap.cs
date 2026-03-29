using System.Text;
using System.Threading.Tasks;

namespace WslContainerMcp.Runtime;

/// <summary>
/// Configures the Podman storage layout inside WSL, installs Podman if missing,
/// and ensures the agent container image is present.
/// </summary>
internal static class PodmanBootstrap
{
    private const string ImageName = "wsl-sandbox-mcp-agent:latest";

    private static string Q(string s) => ProcessExec.Q(s);

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
                const string msg =
                    "❌ Could not resolve $HOME inside WSL.\n\n" +
                    "This usually means the WSL distro does not have a user home directory configured.\n\n" +
                    "To fix:\n" +
                    "  1. Open a terminal and run: wsl\n" +
                    "  2. If prompted to create a user, do so.\n" +
                    "  3. Verify your home directory: echo $HOME\n" +
                    "  4. Start this server again.";
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
                var msg =
                    "❌ Could not create required directories inside WSL.\n\n" +
                    $"Details: {mkdirs.stderr.Trim()}\n\n" +
                    "To fix:\n" +
                    $"  1. Open a WSL terminal and run: mkdir -p {baseDir}/podman/graphroot {baseDir}/podman/runroot\n" +
                    "  2. Start this server again.";
                Console.Error.WriteLine($"[Bootstrap] {msg}");
                return (false, "", msg);
            }

            // 3. Write storage.conf (Mode A: stable per-user Podman storage path)
            var confContent =
                "[storage]\n" +
                "driver = \"overlay\"\n" +
                $"graphroot = \"{graphRoot}\"\n" +
                $"runroot = \"{runRoot}\"\n";

            if (!WriteToWsl(storageConf, confContent))
            {
                const string msg =
                    "❌ Failed to write the Podman storage configuration inside WSL.\n\n" +
                    "This usually means 'base64' is not available in the WSL distro.\n\n" +
                    "To fix:\n" +
                    "  1. Open a WSL terminal and run: sudo apt-get install -y coreutils\n" +
                    "  2. Start this server again.";
                Console.Error.WriteLine($"[Bootstrap] {msg}");
                return (false, "", msg);
            }

            var podmanEnv = $"CONTAINERS_STORAGE_CONF={Q(storageConf)}";

            // 4. Verify or install Podman
            if (!CommandExistsInWsl("podman"))
            {
                Console.Error.WriteLine("[Bootstrap] Podman not found; attempting non-interactive install...");
                if (!TryInstallPodman(out var installMsg))
                {
                    Console.Error.WriteLine($"[Bootstrap] Podman install failed: {installMsg}");
                    return (false, podmanEnv, installMsg);
                }
            }

            if (ProcessExec.WslSh($"{podmanEnv} podman --version", 10).exitCode != 0)
            {
                const string msg =
                    "❌ Podman is installed but 'podman --version' failed.\n\n" +
                    "Podman may be installed but not functional.\n\n" +
                    "To fix:\n" +
                    "  1. Open a WSL terminal and run: podman --version\n" +
                    "  2. If it fails, try reinstalling: sudo apt-get install --reinstall podman\n" +
                    "  3. Start this server again.";
                Console.Error.WriteLine($"[Bootstrap] {msg}");
                return (false, podmanEnv, msg);
            }

            var podmanInfo = ProcessExec.WslSh($"{podmanEnv} podman info", 30);
            if (podmanInfo.exitCode != 0)
            {
                var msg =
                    "❌ Podman is installed but 'podman info' failed.\n\n" +
                    "This usually means a missing kernel feature (cgroups v2) or a user namespace issue.\n\n" +
                    $"Error details: {podmanInfo.stderr.Trim()}\n\n" +
                    "To fix:\n" +
                    "  1. Ensure your WSL distro is Ubuntu 22.04+ or another distro with cgroups v2.\n" +
                    "  2. Open a WSL terminal and run: podman info\n" +
                    "  3. If you see 'cgroup' errors, update your WSL kernel: wsl --update\n" +
                    "  4. Start this server again.";
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
                    var msg =
                        $"❌ The container image '{ImageName}' was not found after a successful build.\n\n" +
                        "This may indicate a Podman storage configuration issue.\n\n" +
                        "To fix:\n" +
                        "  1. Open a WSL terminal and run: podman images\n" +
                        "  2. Check that the Podman storage configuration is correct:\n" +
                        $"     cat ~/.wsl-sandbox-mcp/storage.conf\n" +
                        "  3. Start this server again.";
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
            var exMsg =
                "❌ An unexpected error occurred during environment setup.\n\n" +
                $"Error: {ex.Message}\n\n" +
                "Please check the server's error output for details and try starting again.";
            return (false, "", exMsg);
        }
    }

    private static bool CommandExistsInWsl(string name)
        => ProcessExec.WslSh($"command -v {Q(name)} >/dev/null 2>&1", 10).exitCode == 0;

    private static bool TryInstallPodman(out string issueMsg)
    {
        if (ProcessExec.WslSh("sudo -n true", 10).exitCode != 0)
        {
            issueMsg =
                "❌ Podman is not installed and cannot be installed automatically.\n\n" +
                "Automatic installation requires passwordless sudo, which is not configured.\n\n" +
                "To fix:\n" +
                "  Option A — Install Podman manually:\n" +
                "    1. Open a WSL terminal and run: sudo apt-get install -y podman\n" +
                "    2. Start this server again.\n\n" +
                "  Option B — Enable passwordless sudo, then restart this server:\n" +
                "    1. Open a WSL terminal and run: sudo visudo\n" +
                "    2. Add the line: <username> ALL=(ALL) NOPASSWD: ALL";
            Console.Error.WriteLine($"[Bootstrap] {issueMsg}");
            return false;
        }

        var pm = DetectPackageManager();
        if (string.IsNullOrEmpty(pm))
        {
            issueMsg =
                "❌ Podman is not installed and no supported package manager was found.\n\n" +
                "Looked for: apt-get, dnf, apk\n\n" +
                "To fix:\n" +
                "  1. Open a WSL terminal.\n" +
                "  2. Install Podman using your distro's package manager.\n" +
                "  3. Verify: podman --version\n" +
                "  4. Start this server again.";
            Console.Error.WriteLine($"[Bootstrap] {issueMsg}");
            return false;
        }

        Console.Error.WriteLine($"[Bootstrap] Installing Podman via {pm}...");
        (int exitCode, string stdout, string stderr) result = pm switch
        {
            "apt-get" => InstallWithApt("podman"),
            "dnf"     => ProcessExec.WslSh("sudo -n dnf install -y podman", 900),
            "apk"     => ProcessExec.WslSh("sudo -n apk add podman", 900),
            _         => (exitCode: 1, stdout: "", stderr: "Unknown package manager"),
        };

        if (result.exitCode != 0)
        {
            issueMsg =
                $"❌ Automatic Podman installation via {pm} failed.\n\n" +
                $"Error details: {result.stderr?.Trim()}\n\n" +
                "To fix:\n" +
                "  1. Open a WSL terminal.\n" +
                (pm == "apt-get"
                    ? "  2. Run: sudo apt-get update && sudo apt-get install -y podman\n"
                    : pm == "dnf"
                    ? "  2. Run: sudo dnf install -y podman\n"
                    : "  2. Run: sudo apk add podman\n") +
                "  3. Verify: podman --version\n" +
                "  4. Start this server again.";
            Console.Error.WriteLine($"[Bootstrap] {issueMsg}");
            return false;
        }

        issueMsg = "";
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
            issueMsg =
                "❌ Cannot map the container directory to a WSL /mnt/... path.\n\n" +
                "This usually means the workspace is not on a drive-letter path.\n\n" +
                "To fix:\n" +
                "  • Ensure this server is run from a standard Windows drive (e.g. C:\\, D:\\).\n" +
                "  • UNC paths (\\\\server\\share\\) are not supported.";
            Console.Error.WriteLine($"[Bootstrap] Path mapping failed: {containerDirWin}");
            return false;
        }

        Console.Error.WriteLine($"[Bootstrap] Building {ImageName} from {containerDirWin}...");
        var r = ProcessExec.WslSh($"{podmanEnv} podman build -t {Q(ImageName)} {Q(wslPath)}", 900);
        if (r.exitCode != 0)
        {
            issueMsg =
                $"❌ Building the container image '{ImageName}' failed.\n\n" +
                $"Error details: {r.stderr?.Trim()}\n\n" +
                "To fix:\n" +
                $"  1. Check the Dockerfile at: {containerDirWin}\n" +
                "  2. Open a WSL terminal and run:\n" +
                $"     podman build -t {ImageName} {wslPath}\n" +
                "  3. Ensure the WSL distro has internet access for downloading packages.\n" +
                "  4. Start this server again.";
            Console.Error.WriteLine($"[Bootstrap] Build error: {r.stderr?.Trim()}");
            return false;
        }
        issueMsg = "";
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

