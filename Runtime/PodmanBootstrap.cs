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
                var msg = "BOOTSTRAP_MKDIR_FAILED: Could not create required directories inside WSL.\n" +
                          $"Details: {mkdirs.stderr.Trim()}";
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
