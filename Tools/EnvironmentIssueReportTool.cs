using System.ComponentModel;
using System.Threading;
using ModelContextProtocol.Server;
using WslContainerMcp.Runtime;

namespace WslContainerMcp.Tools;

/// <summary>
/// Fallback MCP tool exposed when the container environment is not ready.
/// Calling it returns a human-readable description of the failure so the user
/// can diagnose and fix the problem without leaving their AI chat.
/// </summary>
[McpServerToolType]
public sealed class EnvironmentIssueReportTool(BootstrapResult bootstrap)
{
    [McpServerTool(Name = "environment_issue_report")]
    [Description(
        "Returns a description of why the Linux container environment is not available. " +
        "Call this to find out what needs to be fixed " +
        "(e.g. WSL not installed, Podman missing, image build failed).")]
    public string GetReport() =>
        bootstrap.IssueReport ?? "No issue detected; the environment appears to be healthy.";
}
