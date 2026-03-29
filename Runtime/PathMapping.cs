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
    /// <list type="bullet">
    ///   <item>Absolute Linux paths (starting with <c>/</c>) are accepted and normalised.</item>
    ///   <item>Relative paths are accepted and resolved relative to <c>/workspace</c> by the caller.</item>
    ///   <item>Paths containing <c>..</c>, Windows drive prefixes, or Windows-style absolute roots are rejected.</item>
    /// </list>
    /// Returns false and leaves <paramref name="sanitized"/> as "." when the path is rejected.
    /// </summary>
    public static bool TrySanitizeCwd(string? cwd, out string sanitized)
    {
        sanitized = ".";
        if (string.IsNullOrWhiteSpace(cwd) || cwd.Trim() == ".") return true;

        // Allow absolute Linux paths (start with '/').
        if (cwd.StartsWith('/'))
        {
            // Reject traversal segments.
            var segs = cwd.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segs.Any(s => s == "..")) return false;
            sanitized = "/" + string.Join("/", segs);
            return true;
        }

        // Reject Windows-style absolute paths (e.g. C:\...) or UNC paths.
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
