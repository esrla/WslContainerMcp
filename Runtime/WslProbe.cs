namespace WslContainerMcp.Runtime;

/// <summary>Probes whether Windows Subsystem for Linux is installed and usable.</summary>
internal static class WslProbe
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
