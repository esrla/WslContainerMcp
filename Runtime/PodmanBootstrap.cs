using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace WslContainerMcp.Runtime;

/// <summary>
/// Configures the Podman storage layout inside WSL, installs Podman if missing,
/// builds the agent image, and ensures the persistent Linux container is running.
/// </summary>
internal static class PodmanBootstrap
{
    private const string ImageName     = "wsl-sandbox-mcp-agent:latest";
    private const string ContainerName = "wsl-sandbox-mcp-persistent";

    private static string Q(string s) => ProcessExec.Q(s);

    public static Task<(bool ready, string podmanEnv, string containerName, string? issueReport)>
        RunAsync(string containerDirWin, string linuxContainerWin, bool allowNetwork)
        => Task.Run(() => Run(containerDirWin, linuxContainerWin, allowNetwork));

    private static (bool ready, string podmanEnv, string containerName, string? issueReport)
        Run(string containerDirWin, string linuxContainerWin, bool allowNetwork)
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
                return (false, "", "", msg);
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
                return (false, "", "", msg);
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
                return (false, "", "", msg);
            }

            var podmanEnv = $"CONTAINERS_STORAGE_CONF={Q(storageConf)}";

            // 4. Verify or install Podman
            if (!CommandExistsInWsl("podman"))
            {
                Console.Error.WriteLine("[Bootstrap] Podman not found; attempting non-interactive install...");
                if (!TryInstallPodman(out var installMsg))
                {
                    Console.Error.WriteLine($"[Bootstrap] Podman install failed: {installMsg}");
                    return (false, podmanEnv, "", installMsg);
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
                return (false, podmanEnv, "", msg);
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
                return (false, podmanEnv, "", msg);
            }

            // 5. Ensure agent image exists
            if (ProcessExec.WslSh($"{podmanEnv} podman image exists {Q(ImageName)}", 10).exitCode != 0)
            {
                Console.Error.WriteLine($"[Bootstrap] Image {ImageName} missing; building...");
                if (!BuildImage(podmanEnv, containerDirWin, out var buildMsg))
                {
                    Console.Error.WriteLine($"[Bootstrap] Image build failed: {buildMsg}");
                    return (false, podmanEnv, "", buildMsg);
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
                    return (false, podmanEnv, "", msg);
                }
            }

            // 6. Ensure the persistent container exists and is running.
            //    The container bind-mounts linux-container/workspace → /workspace and
            //    linux-container/home → /home so the full user environment is visible
            //    from Windows at %USERPROFILE%\.wsl-sandbox-mcp\linux-container\.
            if (!EnsurePersistentContainer(podmanEnv, linuxContainerWin, allowNetwork, out var containerMsg))
            {
                Console.Error.WriteLine($"[Bootstrap] Persistent container setup failed: {containerMsg}");
                return (false, podmanEnv, "", containerMsg);
            }

            Console.Error.WriteLine("[Bootstrap] All checks passed.");
            return (true, podmanEnv, ContainerName, null);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Bootstrap] Exception: {ex.Message}");
            var exMsg =
                "❌ An unexpected error occurred during environment setup.\n\n" +
                $"Error: {ex.Message}\n\n" +
                "Please check the server's error output for details and try starting again.";
            return (false, "", "", exMsg);
        }
    }

    private static bool CommandExistsInWsl(string name)
        => ProcessExec.WslSh($"command -v {Q(name)} >/dev/null 2>&1", 10).exitCode == 0;

    /// <summary>
    /// Ensures the persistent container <see cref="ContainerName"/> exists and is running.
    /// <para>
    /// On first run the container is created with two bind mounts so that the Linux environment
    /// is fully user-inspectable from Windows:
    /// <list type="bullet">
    ///   <item><c>linux-container/workspace</c> → <c>/workspace</c> — project files</item>
    ///   <item><c>linux-container/home</c>      → <c>/home</c>      — user home directories</item>
    /// </list>
    /// These subdirectories of the Windows-side <c>linux-container</c> folder persist across
    /// server restarts and container restarts. Packages installed with <c>apt</c> / other package
    /// managers live inside the container's overlay layer (Podman graphroot) and also persist as
    /// long as the container is not removed.
    /// </para>
    /// <para>
    /// <b>Changing <c>--no-network</c>:</b> The network flag is applied only when the container is
    /// first created. To change it, remove the container manually
    /// (<c>podman rm -f wsl-sandbox-mcp-persistent</c>) and restart the server.
    /// </para>
    /// </summary>
    private static bool EnsurePersistentContainer(
        string podmanEnv, string linuxContainerWin, bool allowNetwork, out string issueMsg)
    {
        // Check whether the container already exists
        var inspectResult = ProcessExec.WslSh(
            $"{podmanEnv} podman inspect --format {Q("{{.State.Status}}")} {Q(ContainerName)}",
            15);

        if (inspectResult.exitCode == 0)
        {
            var status = inspectResult.stdout.Trim();
            if (status == "running")
            {
                Console.Error.WriteLine($"[Bootstrap] Persistent container '{ContainerName}' is already running.");
                issueMsg = "";
                return true;
            }

            // Container exists but is not running — try to start it.
            Console.Error.WriteLine($"[Bootstrap] Persistent container '{ContainerName}' exists (status: {status}); starting...");
            var startResult = ProcessExec.WslSh($"{podmanEnv} podman start {Q(ContainerName)}", 30);
            if (startResult.exitCode == 0)
            {
                Console.Error.WriteLine($"[Bootstrap] Persistent container '{ContainerName}' started.");
                issueMsg = "";
                return true;
            }

            // Start failed — remove the container and recreate it below.
            Console.Error.WriteLine($"[Bootstrap] Could not start existing container: {startResult.stderr.Trim()}. Removing and recreating...");
            ProcessExec.WslSh($"{podmanEnv} podman rm -f {Q(ContainerName)}", 15);
        }

        // Map the Windows-side linux-container subdirectories to WSL paths.
        var wslWorkspace = PathMapping.ToWslPath(Path.Combine(linuxContainerWin, "workspace"));
        var wslHome      = PathMapping.ToWslPath(Path.Combine(linuxContainerWin, "home"));

        if (string.IsNullOrWhiteSpace(wslWorkspace) || string.IsNullOrWhiteSpace(wslHome))
        {
            issueMsg =
                "❌ Cannot map the linux-container directories to WSL /mnt/... paths.\n\n" +
                "This usually means the server is not running from a standard Windows drive (e.g. C:\\).\n\n" +
                "To fix:\n" +
                "  • Ensure this server is run from a drive-letter path.\n" +
                "  • UNC paths (\\\\server\\share\\) are not supported.";
            Console.Error.WriteLine($"[Bootstrap] Path mapping failed for: {linuxContainerWin}");
            return false;
        }

        // Create and start the persistent container.
        var networkFlag = allowNetwork ? "" : "--network none";
        var createParts = new[]
        {
            podmanEnv,
            "podman create",
            $"--name {Q(ContainerName)}",
            networkFlag,
            $"-v {Q(wslWorkspace + ":/workspace:rw")}",
            $"-v {Q(wslHome + ":/home:rw")}",
            Q(ImageName),
            "sleep infinity",
        };
        var createScript = string.Join(" ", createParts.Where(p => !string.IsNullOrEmpty(p))).Trim();

        Console.Error.WriteLine($"[Bootstrap] Creating persistent container '{ContainerName}'...");
        var createResult = ProcessExec.WslSh(createScript, 30);
        if (createResult.exitCode != 0)
        {
            issueMsg =
                $"❌ Failed to create the persistent container '{ContainerName}'.\n\n" +
                $"Error details: {createResult.stderr.Trim()}\n\n" +
                "To fix:\n" +
                $"  1. Open a WSL terminal and run: podman ps -a\n" +
                $"  2. If a stale container exists, remove it: podman rm -f {ContainerName}\n" +
                "  3. Start this server again.";
            Console.Error.WriteLine($"[Bootstrap] Create failed: {createResult.stderr.Trim()}");
            return false;
        }

        Console.Error.WriteLine($"[Bootstrap] Starting persistent container '{ContainerName}'...");
        var startResult2 = ProcessExec.WslSh($"{podmanEnv} podman start {Q(ContainerName)}", 30);
        if (startResult2.exitCode != 0)
        {
            issueMsg =
                $"❌ Failed to start the persistent container '{ContainerName}'.\n\n" +
                $"Error details: {startResult2.stderr.Trim()}\n\n" +
                "To fix:\n" +
                $"  1. Open a WSL terminal and run: podman start {ContainerName}\n" +
                "  2. If it fails, check Podman logs and restart this server.";
            Console.Error.WriteLine($"[Bootstrap] Start failed: {startResult2.stderr.Trim()}");
            return false;
        }

        Console.Error.WriteLine($"[Bootstrap] Persistent container '{ContainerName}' is running.");
        issueMsg = "";
        return true;
    }

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

