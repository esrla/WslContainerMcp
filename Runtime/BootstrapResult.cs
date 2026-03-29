namespace WslContainerMcp.Runtime;

/// <summary>Captures the outcome of the startup bootstrap so it can be shared via DI.</summary>
public sealed class BootstrapResult
{
    public bool    PodmanReady     { get; init; }
    public string  PodmanEnv       { get; init; } = "";
    public string? IssueReport     { get; init; }
    public string  WorkspaceWin    { get; init; } = "";
    public string  OutWin          { get; init; } = "";
    public string  ContainerDirWin { get; init; } = "";
    /// <summary>When false, containers are started with <c>--network none</c>.</summary>
    public bool    AllowNetwork    { get; init; } = true;
}
