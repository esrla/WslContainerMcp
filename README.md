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

All tool calls share a **single named container** (`wsl-sandbox-mcp-persistent`) that is created on first use and reused indefinitely:

- **Installed packages persist.** Running `apt-get install nodejs` makes Node.js available in every subsequent call.
- **Project files persist.** Files created in `/workspace` or `/root` survive between calls and server restarts.
- **Multiple projects coexist.** Use separate sub-directories (e.g. `/workspace/project-a`, `/workspace/project-b`); no separate container is needed per project.
- **The agent does not need to know it is in a container.** From the agent's perspective it is an ordinary Linux machine with a persistent home directory and package manager.

The server attempts to fix problems automatically where possible:
- **WSL not installed** → tries `wsl --install --no-launch` automatically
- **No Linux distro** → tries `wsl --install -d Ubuntu --no-launch` automatically
- **Shell not responding** → tries `wsl --shutdown` and retries automatically
- **Podman not installed** → installs via `apt-get`/`dnf`/`apk` automatically
- **Agent image missing** → builds from the embedded Dockerfile automatically
- **Persistent container stopped** (e.g. after a machine reboot) → restarted automatically on next tool call

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

| Flag            | Description                                                                        |
|-----------------|------------------------------------------------------------------------------------|
| `--no-network`  | Create the persistent container with `--network none` (no internet access)        |

> **Note:** The network flag only takes effect when the container is first created. If the
> container already exists with a different network setting, stop and remove it first (see
> [Resetting the environment](#resetting-the-environment)) and restart the server.

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

Runs a command in the persistent `wsl-sandbox-mcp-agent:latest` container.

### Input

| Parameter   | Type                     | Required | Default | Description                                                                          |
|-------------|--------------------------|----------|---------|--------------------------------------------------------------------------------------|
| `cmd`       | `string`                 | ✓        | —       | Executable to run (e.g. `python3`, `bash`, `node`)                                  |
| `args`      | `string[]`               | ✓        | —       | Arguments to pass to the executable                                                  |
| `cwd`       | `string`                 |          | `"."`   | Working directory. Absolute Linux paths (e.g. `/root/myproject`) are used as-is. Relative paths resolve from `/workspace`. |
| `timeout_s` | `int`                    |          | `120`   | Timeout in seconds (clamped to 1–3600)                                              |
| `env`       | `object<string, string>` |          | `{}`    | Extra environment variables for this command                                         |

`cwd` accepts:
- **Relative paths** (default): resolved against `/workspace`. Must not contain `..` or drive letters.
- **Absolute Linux paths** (e.g. `/root/myproject`, `/tmp/scratch`): used directly. Must not contain `..`.

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
├── workspace\              ← mounted as /workspace inside the container
│   └── out\                ← call metadata JSON files
├── home\                   ← mounted as /root (root user's home directory)
└── container\
    └── Dockerfile          ← auto-extracted; used to build the agent image
```

### Inspecting files from Windows

| Container path | Windows path                                           |
|----------------|--------------------------------------------------------|
| `/workspace`   | `%USERPROFILE%\.wsl-sandbox-mcp\workspace\`           |
| `/root`        | `%USERPROFILE%\.wsl-sandbox-mcp\home\`                |

Both directories can be opened directly in Windows Explorer. Any file written to `/workspace` or `/root` during a run is **immediately visible from Windows**.

For example:
- A file at `/workspace/project/output.txt` → `%USERPROFILE%\.wsl-sandbox-mcp\workspace\project\output.txt`
- A file at `/root/.bashrc` → `%USERPROFILE%\.wsl-sandbox-mcp\home\.bashrc`

### Inspecting installed software (Podman overlay storage)

Software installed via `apt-get` (e.g. Node.js) is stored in the container's overlay filesystem, which persists as long as the container exists. This storage is not directly browsable in the same friendly way, but it is accessible via `\\wsl$`:

```
\\wsl$\<DistroName>\home\<linuxuser>\.wsl-sandbox-mcp\podman\
├── graphroot\   ← image layers and container writable layer
└── runroot\     ← runtime state
```

## Resetting the environment

To start with a clean environment (for example, after a Dockerfile update or to discard all installed packages), stop and remove the persistent container from a WSL shell:

```bash
# In a WSL terminal:
CONTAINERS_STORAGE_CONF=~/.wsl-sandbox-mcp/storage.conf podman stop wsl-sandbox-mcp-persistent
CONTAINERS_STORAGE_CONF=~/.wsl-sandbox-mcp/storage.conf podman rm   wsl-sandbox-mcp-persistent
```

Then restart the MCP server. It will automatically create a fresh container from the latest image on the next tool call.

> **Image rebuild:** If the `Dockerfile` changed since the container was created, rebuild the image first:
> ```bash
> CONTAINERS_STORAGE_CONF=~/.wsl-sandbox-mcp/storage.conf \
>   podman build -t wsl-sandbox-mcp-agent:latest ~/.wsl-sandbox-mcp/container/
> ```
> Then stop/remove and recreate the container as above.

## Long-running processes and port exposure

The `run_linux_cli` tool runs commands synchronously and returns when they finish (or time out). It does **not** manage background processes or expose ports automatically.

**Timed-out commands:** If a command is killed by the timeout, the container keeps running but the command's process may still be alive inside it. Over time, orphaned processes can accumulate. Inspect and clean them up with:
```bash
cmd: bash
args: ["-c", "ps aux"]
```

**To run a long-lived process** (e.g. a development web server):
1. Start it with `nohup`, `screen`, or `tmux` via `run_linux_cli` so it survives the exec:
   ```bash
   # Example: start a Node.js server in the background
   cmd: bash
   args: ["-c", "nohup npm run dev > /root/dev-server.log 2>&1 &"]
   ```
2. Check its output later with `tail /root/dev-server.log`.

**To expose ports to the Windows host**, the container must be recreated with `-p` mappings.
Stop and remove the container, then add a port mapping by manually running in a WSL terminal:

```bash
# Resolve the Windows user-profile path to a WSL-compatible mount path.
# Run this in your WSL terminal:
WSL_USERPROFILE=$(wslpath -a "$USERPROFILE")

CONTAINERS_STORAGE_CONF=~/.wsl-sandbox-mcp/storage.conf \
  podman run -d \
    --name wsl-sandbox-mcp-persistent \
    -v "${WSL_USERPROFILE}/.wsl-sandbox-mcp/workspace:/workspace:rw" \
    -v "${WSL_USERPROFILE}/.wsl-sandbox-mcp/home:/root:rw" \
    -e HOME=/root \
    -w /workspace \
    -p 3000:3000 \
    wsl-sandbox-mcp-agent:latest \
    sleep infinity
```

Port management and automatic port registration are planned as future enhancements.

## Project Structure

```
WslContainerMcp/
├── Program.cs                  ← Minimal startup (WSL/Podman bootstrap → MCP server)
├── AgentDockerfile.cs          ← Embedded Dockerfile content (self-contained)
├── WslContainerMcp.csproj
├── Runtime/
│   ├── BootstrapResult.cs      ← DI-shared state from startup bootstrap
│   ├── LinuxCliRunner.cs       ← run_linux_cli core logic (persistent container)
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

