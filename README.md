# WslContainerMcp

A Windows-only MCP server that gives any MCP-aware AI agent full access to a **persistent** Linux environment running inside WSL via Podman.

## How It Works

The server communicates over **stdio** (stdin/stdout) and always exposes exactly **one tool** depending on whether the environment is ready:

| Environment state               | Tool exposed                  |
|---------------------------------|-------------------------------|
| Everything ready                | `run_linux_cli`               |
| Any issue (WSL, Podman, etc.)   | `environment_issue_report`    |

Calling `environment_issue_report` returns a plain-language description of what went wrong and step-by-step instructions to fix it — so users can resolve issues without leaving their AI chat.

### Persistent Linux environment

`run_linux_cli` executes commands inside a **single named container** (`wsl-sandbox-mcp-persistent`) that is started once at server startup and reused for every subsequent call. This means:

- **Installed software persists** — install Node.js with `apt`, and it is still there next call.
- **Filesystem state persists** — files written anywhere in the Linux environment survive across invocations and server restarts (subject to container lifecycle, see below).
- **Multiple projects coexist** — use `/workspace/project-a`, `/workspace/project-b`, or any directory under `/home` or elsewhere. There is no artificial per-project isolation.
- **The agent experiences a normal Linux machine** — it does not need to know it is inside a container.

The server attempts to fix problems automatically where possible:
- **WSL not installed** → tries `wsl --install --no-launch` automatically
- **No Linux distro** → tries `wsl --install -d Ubuntu --no-launch` automatically
- **Shell not responding** → tries `wsl --shutdown` and retries automatically
- **Podman not installed** → installs via `apt-get`/`dnf`/`apk` automatically
- **Agent image missing** → builds from the embedded Dockerfile automatically
- **Persistent container missing or stopped** → creates/restarts it automatically

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

| Flag            | Description                                                                         |
|-----------------|-------------------------------------------------------------------------------------|
| `--no-network`  | Create the container with `--network none` (no internet access). Only takes effect  |
|                 | when the container is first created. To change this on an existing container,       |
|                 | run `podman rm -f wsl-sandbox-mcp-persistent` in WSL, then restart the server.     |

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

Runs a command inside the persistent `wsl-sandbox-mcp-persistent` container via `podman exec`.

### Input

| Parameter   | Type                     | Required | Default | Description                                                                |
|-------------|--------------------------|----------|---------|----------------------------------------------------------------------------|
| `cmd`       | `string`                 | ✓        | —       | Executable to run (e.g. `python3`, `bash`, `node`)                        |
| `args`      | `string[]`               | ✓        | —       | Arguments to pass to the executable                                        |
| `cwd`       | `string`                 |          | `"."`   | Working directory. Absolute Linux paths (e.g. `/home/user/project`) or    |
|             |                          |          |         | relative paths resolved under `/workspace` (e.g. `myapp` → `/workspace/myapp`). |
| `timeout_s` | `int`                    |          | `120`   | Timeout in seconds (clamped to 1–3600)                                    |
| `env`       | `object<string, string>` |          | `{}`    | Extra environment variables for this command                               |

`cwd` paths containing `..` are rejected. Windows-style absolute paths and drive prefixes are rejected.

### Output (JSON)

| Field           | Type     | Description                                                 |
|-----------------|----------|-------------------------------------------------------------|
| `exit_code`     | `int`    | Process exit code (0 = success)                             |
| `stdout`        | `string` | Captured standard output                                    |
| `stderr`        | `string` | Captured standard error                                     |
| `timed_out`     | `bool`   | Whether the command was killed due to timeout               |
| `artifact_meta` | `string` | Relative path to call metadata JSON (optional)             |

## Filesystem & File Access

All persistent data is stored under `%USERPROFILE%\.wsl-sandbox-mcp\` (one environment per Windows user):

```
%USERPROFILE%\.wsl-sandbox-mcp\
├── linux-container\            ← the persistent Linux environment (user-browsable from Windows)
│   ├── workspace\              ← bind-mounted as /workspace inside the container
│   ├── home\                   ← bind-mounted as /home inside the container
│   └── out\                    ← per-call metadata JSON files
└── container\
    └── Dockerfile              ← auto-extracted; used to build the agent image
```

### Browsing from Windows

Open `%USERPROFILE%\.wsl-sandbox-mcp\linux-container\` in Windows Explorer to inspect:

- **`workspace\`** — corresponds to `/workspace` inside Linux (project files, repos, build output)
- **`home\`** — corresponds to `/home` inside Linux (user home directories, dotfiles, caches)

For example, a file written to `/workspace/result.txt` in Linux is readable on Windows at:
```
%USERPROFILE%\.wsl-sandbox-mcp\linux-container\workspace\result.txt
```

### System directories (installed packages)

Packages installed with `apt` / `dnf` / `apk` and other system-level changes (e.g. files in
`/usr`, `/etc`, `/var`) live inside the container's **overlay layer** in Podman's graphroot.
These changes persist as long as the container is not removed, and are accessible via the WSL
network path:

```
\\wsl$\<DistroName>\home\<linuxuser>\.wsl-sandbox-mcp\podman\graphroot\
```

Open this path directly in Windows Explorer.

### Container lifecycle

- The container is **created once** at server startup and **reused** for all subsequent calls.
- The container survives server restarts — it is only stopped/removed if you do so manually.
- To reset the environment completely: run `podman rm -f wsl-sandbox-mcp-persistent` inside
  WSL, then restart the server. A fresh container will be created automatically.

### Long-lived processes and port exposure

Commands that start background services (e.g. a web server with `node server.js &`) will
keep running in the container after `run_linux_cli` returns, because the container stays alive.
However, **port forwarding** from the container to the Windows host (so you can open
`http://localhost:3000` in a browser) requires additional Podman `-p` flags at container
creation time, which is not yet configured automatically. To use this today:

1. Remove the container: `podman rm -f wsl-sandbox-mcp-persistent` (inside WSL)
2. Recreate it manually with port forwarding, e.g.:
   ```sh
   podman create --name wsl-sandbox-mcp-persistent \
     -v /mnt/c/Users/<user>/.wsl-sandbox-mcp/linux-container/workspace:/workspace:rw \
     -v /mnt/c/Users/<user>/.wsl-sandbox-mcp/linux-container/home:/home:rw \
     -p 3000:3000 \
     wsl-sandbox-mcp-agent:latest sleep infinity
   podman start wsl-sandbox-mcp-persistent
   ```
3. The server will detect the running container and use it normally.

Automatic port-forwarding support is tracked as a follow-up improvement.

## Project Structure

```
WslContainerMcp/
├── Program.cs                  ← Minimal startup (WSL/Podman bootstrap → MCP server)
├── AgentDockerfile.cs          ← Embedded Dockerfile content (self-contained)
├── WslContainerMcp.csproj
├── Runtime/
│   ├── BootstrapResult.cs      ← DI-shared state from startup bootstrap
│   ├── LinuxCliRunner.cs       ← run_linux_cli core logic (podman exec)
│   ├── PathMapping.cs          ← Windows ↔ WSL path conversion + cwd sanitization
│   ├── PodmanBootstrap.cs      ← Podman setup: storage config, install, image build,
│   │                              persistent container create/start
│   ├── ProcessExec.cs          ← Low-level process/WSL execution helpers
│   ├── WslBootstrap.cs         ← WSL availability checks + auto-fix attempts
│   └── WslProbe.cs             ← Low-level WSL probes (no side effects)
├── Tools/
│   ├── RunLinuxCliTool.cs      ← MCP tool: run_linux_cli
│   └── EnvironmentIssueReportTool.cs ← MCP tool: environment_issue_report (fallback)
└── Container/
    └── Dockerfile              ← Source for wsl-sandbox-mcp-agent:latest
```

