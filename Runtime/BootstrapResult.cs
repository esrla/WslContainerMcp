namespace WslContainerMcp.Runtime;

/// <summary>Captures the outcome of the startup bootstrap so it can be shared via DI.</summary>
public sealed class BootstrapResult
{
    public bool    PodmanReady            { get; init; }
    public string  PodmanEnv              { get; init; } = "";
    public string? IssueReport            { get; init; }
    /// <summary>
    /// Windows path to the persistent Linux environment directory
    /// (<c>%USERPROFILE%\.wsl-sandbox-mcp\linux-container</c>).
    /// Subdirectories <c>workspace</c> and <c>home</c> are bind-mounted into
    /// the persistent container at <c>/workspace</c> and <c>/home</c>.
    /// Users can browse the full environment from Windows Explorer.
    /// </summary>
    public string  LinuxContainerWin      { get; init; } = "";
    /// <summary>Windows path where per-call metadata JSON files are written.</summary>
    public string  OutWin                 { get; init; } = "";
    public string  ContainerDirWin        { get; init; } = "";
    /// <summary>Name of the persistent Podman container used for all executions.</summary>
    public string  PersistentContainerName { get; init; } = "";
    /// <summary>When false, the persistent container is created with <c>--network none</c>.</summary>
    public bool    AllowNetwork           { get; init; } = true;
}
