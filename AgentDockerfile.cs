namespace WslContainerMcp;

/// <summary>
/// Embedded Dockerfile for the <c>wsl-sandbox-mcp-agent:latest</c> image.
/// Bundled as a string constant so the self-contained executable is fully self-sufficient.
///
/// This image is the base for the persistent container. It includes common development
/// tools so the agent can work without needing to install them on every fresh environment.
/// Additional software (e.g. Node.js, Rust, Go) can be installed with <c>apt-get</c> or
/// the appropriate installer inside the running container; those installations persist
/// because the container is reused across all tool calls.
/// </summary>
internal static class AgentDockerfile
{
    public const string Content =
        "FROM ubuntu:24.04\n" +
        "ENV DEBIAN_FRONTEND=noninteractive\n" +
        "RUN apt-get update && \\\n" +
        "    apt-get install -y --no-install-recommends \\\n" +
        "        python3 python3-pip python3-venv \\\n" +
        "        curl ca-certificates tar bash \\\n" +
        "        git build-essential wget \\\n" +
        "        sudo apt-transport-https gnupg \\\n" +
        "        unzip zip \\\n" +
        "        procps && \\\n" +
        "    rm -rf /var/lib/apt/lists/*\n" +
        "WORKDIR /workspace\n";
}
