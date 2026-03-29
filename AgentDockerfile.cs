namespace WslContainerMcp;

/// <summary>
/// Embedded Dockerfile for the <c>wsl-sandbox-mcp-agent:latest</c> image.
/// Bundled as a string constant so the self-contained executable is fully self-sufficient.
/// </summary>
internal static class AgentDockerfile
{
    public const string Content =
        "FROM ubuntu:24.04\n" +
        "ENV DEBIAN_FRONTEND=noninteractive\n" +
        "RUN apt-get update && \\\n" +
        "    apt-get install -y --no-install-recommends \\\n" +
        "        python3 python3-pip curl ca-certificates tar bash && \\\n" +
        "    rm -rf /var/lib/apt/lists/*\n" +
        "WORKDIR /workspace\n";
}
