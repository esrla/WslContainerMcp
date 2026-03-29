namespace WslContainerMcp.Runtime;

/// <summary>
/// Ensures WSL is installed and usable, attempting automatic fixes where possible,
/// and returns a rich user-facing report when something cannot be fixed automatically.
/// </summary>
internal static class WslBootstrap
{
    public static Task<(bool ready, string? issueReport)> EnsureAsync()
        => Task.Run(EnsureInternal);

    private static async Task<(bool ready, string? issueReport)> EnsureInternal()
    {
        // ── 1. WSL callable? ──────────────────────────────────────────────────
        if (!WslProbe.IsWslCallable())
        {
            Console.Error.WriteLine("[WslBootstrap] wsl.exe not callable; attempting install...");
            var install = ProcessExec.Exec("wsl.exe", "--install --no-launch", 120);
            if (install.exitCode == 0)
            {
                return (false,
                    "✅ WSL has been installed automatically.\n" +
                    "⚠️  A Windows restart is required to complete the setup.\n\n" +
                    "To finish:\n" +
                    "  1. Restart Windows.\n" +
                    "  2. Start this server again — it will set up the Linux environment automatically.");
            }

            return (false,
                "❌ WSL (Windows Subsystem for Linux) is not installed on this system.\n\n" +
                "Automatic installation was attempted but failed" +
                (install.exitCode != 0 ? $" (exit code {install.exitCode})." : ".") + "\n\n" +
                "To install WSL manually:\n" +
                "  1. Open PowerShell as Administrator (right-click → \"Run as administrator\").\n" +
                "  2. Run: wsl --install\n" +
                "  3. Restart Windows when prompted.\n" +
                "  4. Start this server again.\n\n" +
                "Reference: https://aka.ms/wslinstall");
        }

        // ── 2. Any distro registered? ─────────────────────────────────────────
        if (!WslProbe.HasAnyDistro())
        {
            Console.Error.WriteLine("[WslBootstrap] No WSL distro found; attempting Ubuntu install...");
            var install = ProcessExec.Exec("wsl.exe", "--install -d Ubuntu --no-launch", 300);
            if (install.exitCode == 0)
            {
                // Verify the distro is usable after installation
                if (WslProbe.HasAnyDistro() && WslProbe.CanRunShell())
                    return (true, null);

                return (false,
                    "✅ Ubuntu has been installed in WSL automatically.\n" +
                    "⚠️  Ubuntu needs to be initialized before it can be used.\n\n" +
                    "To finish:\n" +
                    "  1. Open a terminal and run: wsl\n" +
                    "  2. Create a Linux username and password when prompted.\n" +
                    "  3. Start this server again — Podman will be set up automatically.");
            }

            return (false,
                "❌ WSL is installed but no Linux distribution is registered.\n\n" +
                "Automatic Ubuntu installation was attempted but failed" +
                (install.exitCode != 0 ? $" (exit code {install.exitCode})." : ".") + "\n\n" +
                "To install Ubuntu manually:\n" +
                "  1. Open PowerShell and run: wsl --install -d Ubuntu\n" +
                "  2. Complete the Ubuntu setup when prompted (create username and password).\n" +
                "  3. Start this server again.\n\n" +
                "Alternative: install any distro from the Microsoft Store.");
        }

        // ── 3. Shell usable? ──────────────────────────────────────────────────
        if (!WslProbe.CanRunShell())
        {
            Console.Error.WriteLine("[WslBootstrap] Shell not responding; attempting WSL restart...");
            ProcessExec.Exec("wsl.exe", "--shutdown", 10);
            await Task.Delay(2000);

            if (WslProbe.CanRunShell())
                return (true, null);

            return (false,
                "❌ WSL is installed and has a Linux distribution, but the shell is not responding.\n\n" +
                "A WSL restart was attempted automatically but did not resolve the issue.\n\n" +
                "To fix manually:\n" +
                "  1. Open PowerShell and run: wsl --shutdown\n" +
                "  2. Wait a few seconds, then run: wsl -e sh -lc \"echo ok\"\n" +
                "     It should print: ok\n" +
                "  3. If the distro appears corrupted:\n" +
                "     • Run: wsl --list --verbose    (note the distro name)\n" +
                "     • Run: wsl --unregister <DistroName>\n" +
                "     • Run: wsl --install -d Ubuntu\n" +
                "  4. Start this server again.");
        }

        return (true, null);
    }
}

