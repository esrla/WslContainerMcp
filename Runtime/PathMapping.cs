using System.IO;
using System.Linq;

namespace WslContainerMcp.Runtime;

/// <summary>Windows ↔ WSL path conversion and working-directory sanitization.</summary>
internal static class PathMapping
{
    /// <summary>
    /// Convert a Windows absolute path to its /mnt/&lt;drive&gt;/... WSL equivalent.
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
