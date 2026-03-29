# WslContainerMcp

A Windows-only MCP server that gives any MCP-aware AI agent full access to a Linux container running inside WSL via Podman.

## How It Works

The server communicates over **stdio** (stdin/stdout) and always exposes exactly **one tool** depending on whether the environment is ready:

| Environment state               | Tool exposed                  |
|---------------------------------|-------------------------------|
| Everything ready                | `run_linux_cli`               |
| Any issue (WSL, Podman, etc.)   | `environment_issue_report`    |

Calling `environment_issue_report` returns a plain-language description of what went wrong and step-by-step instructions to fix it — so users can resolve issues without leaving their AI chat.

The server attempts to fix problems automatically where possible:
- **WSL not installed** → tries `wsl --install --no-launch` automatically
- **No Linux distro** → tries `wsl --install -d Ubuntu --no-launch` automatically
- **Shell not responding** → tries `wsl --shutdown` and retries automatically
- **Podman not installed** → installs via `apt-get`/`dnf`/`apk` automatically
- **Agent image missing** → builds from the embedded Dockerfile automatically

## Requirements

- **Windows 10/11** (WSL 2 must be supported)
- WSL 2 (installed automatically on first run if missing)
- A Linux distribution in WSL (Ubuntu is installed automatically if none is present)
- Podman (installed automatically inside WSL if missing, requires passwordless `sudo`)

## Quick Start

```powershell
# Option A: run directly (requires .NET 10 SDK)
dotnet run

# Option B: publish self-contained exe and run it
dotnet publish -c Release
.\bin\Release\net10.0-windows\win-x64\publish\WslContainerMcp.exe
```

### Server Flags

| Flag            | Description                                                          |
|-----------------|----------------------------------------------------------------------|
| `--no-network`  | Start containers with `--network none` (no internet access)         |

### Registering with an MCP Client

Add to your MCP client configuration (e.g. Claude Desktop `claude_desktop_config.json`):

```json
{
  "mcpServers": {
    "WslContainerMcp": {
      "command": "C:\\path\\to\\WslContainerMcp.exe"
    }
  }
}
```

To block container internet access:

```json
{
  "mcpServers": {
    "WslContainerMcp": {
      "command": "C:\\path\\to\\WslContainerMcp.exe",
      "args": ["--no-network"]
    }
  }
}
```

## Tool: `run_linux_cli`

Runs a command inside a fresh `wsl-sandbox-mcp-agent:latest` Podman container.

### Input

| Parameter   | Type                     | Required | Default | Description                                                    |
|-------------|--------------------------|----------|---------|----------------------------------------------------------------|
| `cmd`       | `string`                 | ✓        | —       | Executable to run (e.g. `python3`, `bash`)                    |
| `args`      | `string[]`               | ✓        | —       | Arguments to pass to the executable                            |
| `cwd`       | `string`                 |          | `"."`   | Working directory, relative to workspace root                  |
| `timeout_s` | `int`                    |          | `120`   | Timeout in seconds (clamped to 1–3600)                        |
| `env`       | `object<string, string>` |          | `{}`    | Extra environment variables for the container                  |

`cwd` must be a **relative path**. Absolute paths, drive prefixes, and `..` segments are rejected.

### Output (JSON)

| Field           | Type     | Description                                                 |
|-----------------|----------|-------------------------------------------------------------|
| `exit_code`     | `int`    | Process exit code (0 = success)                             |
| `stdout`        | `string` | Captured standard output                                    |
| `stderr`        | `string` | Captured standard error                                     |
| `timed_out`     | `bool`   | Whether the command was killed due to timeout               |
| `artifact_meta` | `string` | Relative path to call metadata JSON (optional)             |

## Workspace & File Access

All data is stored under `%USERPROFILE%\.wsl-sandbox-mcp\` (one workspace per Windows user — no cross-user mixing):

```
%USERPROFILE%\.wsl-sandbox-mcp\
├── workspace\              ← mounted as /workspace inside every container
│   └── out\                ← call metadata JSON files
├── container\
│   └── Dockerfile          ← auto-extracted; used to build the agent image
```

### Inspecting files from inside the container

The workspace is mounted at `/workspace` inside every container. **Any file written to `/workspace` during a run is immediately visible from Windows** at:

```
%USERPROFILE%\.wsl-sandbox-mcp\workspace\
```

For example, a command that writes to `/workspace/result.txt` can be read on Windows at:
```
%USERPROFILE%\.wsl-sandbox-mcp\workspace\result.txt
```

### Inspecting Podman image storage (via `\\wsl$`)

Podman stores images and layers in a stable per-user directory inside WSL:

```
\\wsl$\<DistroName>\home\<linuxuser>\.wsl-sandbox-mcp\podman\
├── graphroot\   ← image layers
└── runroot\     ← runtime state
```

Open this path directly in Windows Explorer.

## Project Structure

```
WslContainerMcp/
├── Program.cs                  ← Minimal startup (WSL/Podman bootstrap → MCP server)
├── AgentDockerfile.cs          ← Embedded Dockerfile content (self-contained)
├── WslContainerMcp.csproj
├── Runtime/
│   ├── BootstrapResult.cs      ← DI-shared state from startup bootstrap
│   ├── LinuxCliRunner.cs       ← run_linux_cli core logic
│   ├── PathMapping.cs          ← Windows ↔ WSL path conversion + cwd sanitization
│   ├── PodmanBootstrap.cs      ← Podman setup: storage config, install, image build
│   ├── ProcessExec.cs          ← Low-level process/WSL execution helpers
│   ├── WslBootstrap.cs         ← WSL availability checks + auto-fix attempts
│   └── WslProbe.cs             ← Low-level WSL probes (no side effects)
├── Tools/
│   ├── RunLinuxCliTool.cs      ← MCP tool: run_linux_cli
│   └── EnvironmentIssueReportTool.cs ← MCP tool: environment_issue_report (fallback)
└── Container/
    └── Dockerfile              ← Source for wsl-sandbox-mcp-agent:latest
```

