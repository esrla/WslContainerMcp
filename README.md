# WslContainerMcp

A Windows-only MCP server that exposes a single tool — **`run_linux_cli`** — allowing any MCP-aware AI agent to run arbitrary commands inside a disposable Podman Linux container inside WSL.

## How It Works

The server communicates over **stdio** (stdin/stdout) and exposes exactly **one tool** depending on whether the environment is ready:

| Environment state                        | Tool exposed                  |
|------------------------------------------|-------------------------------|
| WSL available + Podman ready             | `run_linux_cli`               |
| WSL available, but Podman not yet ready  | `environment_issue_report`    |

Calling `environment_issue_report` returns a human-readable diagnosis message so you can fix the issue without leaving your AI chat.

## Requirements

- **Windows 10/11** (or Windows Server 2019+) with WSL 2 enabled
- At least one WSL Linux distribution installed (`wsl --install`)
- **Podman** inside WSL (auto-installed on first run if not present, requires passwordless `sudo`)

## Quick Start

```powershell
# Option A: run directly (requires .NET 10 SDK)
dotnet run

# Option B: build self-contained exe and run it
dotnet publish -c Release
.\bin\Release\net10.0-windows\win-x64\publish\WslContainerMcp.exe
```

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

## Tool: `run_linux_cli`

Runs a command inside a fresh `wsl-sandbox-mcp-agent:latest` Podman container and returns the results.

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
| `artifact_tar`  | `string` | Relative path to exported container filesystem tar          |
| `artifact_meta` | `string` | Relative path to JSON metadata file                         |

## Workspace Layout (per-user, no cross-user mixing)

All data is stored under `%USERPROFILE%\.wsl-sandbox-mcp\`:

```
%USERPROFILE%\.wsl-sandbox-mcp\
├── workspace\              ← mounted as /workspace inside every container
│   └── out\                ← artifacts for each tool call
│       ├── <id>.tar        ← exported container filesystem (Mode B)
│       └── <id>.meta.json  ← call metadata (cmd, args, exit_code, timestamps…)
├── container\
│   └── Dockerfile          ← auto-extracted; used to build the agent image
└── storage.conf            (inside WSL) ← stable Podman storage config (Mode A)
```

## Inspecting Container State

### Mode A — Stable Podman Storage (inspect via `\\wsl$`)

Podman is configured to store images and layers in a stable per-user directory inside WSL:

```
\\wsl$\<DistroName>\home\<linuxuser>\.wsl-sandbox-mcp\podman\
├── graphroot\   ← image layers
└── runroot\     ← runtime state
```

Open this path directly in Windows Explorer to inspect the Podman storage at any time.

### Mode B — Full Container Filesystem Export (artifact per call)

After every `run_linux_cli` call, the complete container filesystem is exported as a tar archive:

| Artifact                          | Windows path                                                   |
|-----------------------------------|----------------------------------------------------------------|
| Container filesystem              | `%USERPROFILE%\.wsl-sandbox-mcp\workspace\out\<id>.tar`       |
| Call metadata (JSON)              | `%USERPROFILE%\.wsl-sandbox-mcp\workspace\out\<id>.meta.json` |

The metadata JSON contains: `image`, `cmd`, `args`, `cwd`, `env`, `exit_code`, `timed_out`, `started_ts`, `finished_ts`.

## Project Structure

```
WslContainerMcp/
├── Program.cs                  ← Minimal startup (WSL probe → bootstrap → MCP server)
├── AgentDockerfile.cs          ← Embedded Dockerfile content (self-contained)
├── WslContainerMcp.csproj
├── Runtime/
│   ├── BootstrapResult.cs      ← DI-shared state from startup bootstrap
│   ├── LinuxCliRunner.cs       ← run_linux_cli core logic + Mode B export
│   ├── PathMapping.cs          ← Windows ↔ WSL path conversion + cwd sanitization
│   ├── PodmanBootstrap.cs      ← Podman setup: storage config, install, image build
│   ├── ProcessExec.cs          ← Low-level process/WSL execution helpers
│   └── WslProbe.cs             ← WSL availability checks
├── Tools/
│   ├── RunLinuxCliTool.cs      ← MCP tool: run_linux_cli
│   └── EnvironmentIssueReportTool.cs ← MCP tool: environment_issue_report (fallback)
└── Container/
    └── Dockerfile              ← Source for wsl-sandbox-mcp-agent:latest
```

## Troubleshooting

### `WSL_NOT_AVAILABLE` – wsl.exe not found or not callable

Enable WSL in Windows Features and restart, or run:
```powershell
wsl --install
```

### `WSL_NO_DISTRO` – no Linux distribution registered

Install a distro:
```powershell
wsl --install -d Ubuntu
```

### `WSL_SHELL_FAILED` – shell execution failed

Check the health of your default distro:
```powershell
wsl --status
wsl -e sh -lc "echo ok"
```

### `SUDO_NOT_NONINTERACTIVE` – can't install Podman automatically

Configure passwordless sudo inside WSL or install Podman manually:
```bash
sudo apt-get install -y podman
```

### `PODMAN_INFO_FAILED` – Podman installed but not functional

This usually means a missing kernel feature (cgroups v2) or user-namespace issue.
Check inside WSL:
```bash
podman info
```
Ensure your WSL distro is Ubuntu 22.04+ or another distro with cgroups v2 support.

### `IMAGE_BUILD_FAILED` – container image build failed

Check the Dockerfile at:
```
%USERPROFILE%\.wsl-sandbox-mcp\container\Dockerfile
```
And run manually inside WSL:
```bash
podman build -t wsl-sandbox-mcp-agent:latest <path-to-dockerfile-dir>
```

