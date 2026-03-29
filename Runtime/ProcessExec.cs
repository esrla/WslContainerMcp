using System.Diagnostics;
using System.Text;
using System.Threading;

namespace WslContainerMcp.Runtime;

/// <summary>Spawns processes and runs commands inside the default WSL distro.</summary>
internal static class ProcessExec
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
