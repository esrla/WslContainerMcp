namespace WslContainerMcp.Runtime;

/// <summary>Captures the outcome of the startup bootstrap so it can be shared via DI.</summary>
public sealed class BootstrapResult
{
    public bool    PodmanReady     { get; init; }
    public string  PodmanEnv       { get; init; } = "";
    public string? IssueReport     { get; init; }
    public string  WorkspaceWin    { get; init; } = "";
    /// <summary>
    /// Windows path to the persistent home directory, mounted as <c>/root</c> inside the
    /// container so the root user's home is directly inspectable from Windows Explorer.
    /// </summary>
    public string  HomeWin         { get; init; } = "";
    public string  OutWin          { get; init; } = "";
    public string  ContainerDirWin { get; init; } = "";
    /// <summary>When false, the persistent container is created with <c>--network none</c>.</summary>
    public bool    AllowNetwork    { get; init; } = true;
}
