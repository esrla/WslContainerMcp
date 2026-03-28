Prompt: Build “Wsl sandbox mcp” (C# MCP server over stdio; WSL-gated; 0 tools or 1 tool; inspectable container state)

Goal
Create a Windows-only MCP server executable named Wsl sandbox mcp implemented in C#/.NET that communicates via stdio (stdin/stdout). The server must expose no tools unless Windows Subsystem for Linux (WSL) is available and usable. In the “WSL usable” case, the server must ensure a Podman-based Linux container execution environment inside WSL and expose exactly one tool: run_linux_cli.

Hard rules
1) If WSL is not usable → MCP server reports zero tools (empty tool list).
2) If WSL is usable and Podman/container environment is ready → MCP server reports exactly one tool: run_linux_cli.
3) If any prerequisite for the “usable” path fails → zero tools.
4) No other tools and no diagnostic tools. Only run_linux_cli or nothing.
5) The system must support post-run inspection of what got installed/changed inside the Linux environment.

WSL usability definition (must be implemented)
WSL is “usable” only if:
- wsl.exe is callable (wsl --version when available).
- At least one distro exists (wsl -l -v returns at least one entry).
- A trivial command succeeds: wsl -e sh -lc "echo ok" exits 0.
If any probe fails → WSL not usable.

Inspectable container state (must be implemented)
Support BOTH inspection modes:

Mode A: Stable Podman storage path inside WSL (inspect via \wsl$)
- Configure Podman to use a stable per-user storage directory inside WSL:
  - WSL path: /home/<linuxuser>/.wsl-sandbox-mcp/podman
- Implement by generating a storage.conf file and forcing Podman to use it for all invocations:
  - Set CONTAINERS_STORAGE_CONF=/home/<linuxuser>/.wsl-sandbox-mcp/storage.conf
  - In storage.conf, set:
    - graphroot = "/home/<linuxuser>/.wsl-sandbox-mcp/podman/graphroot"
    - runroot  = "/home/<linuxuser>/.wsl-sandbox-mcp/podman/runroot"
- Ensure the MCP server applies this env var to every Podman call (bootstrap + tool execution).
- README must document how to inspect from Windows using \wsl$\<DistroName>\home\<linuxuser>\.wsl-sandbox-mcp\podman.

Mode B: Export full container filesystem after each run (artifact)
- For each run_linux_cli call:
  - Use deterministic container name: wsl-sandbox-mcp-<tool_call_id>
  - After command finishes (success or failure), export filesystem:
    - podman export <name> -o /workspace/out/<tool_call_id>.tar
  - Write manifest:
    - /workspace/out/<tool_call_id>.meta.json containing { image, cmd, args, cwd, env, exit_code, started_ts, finished_ts }
  - Always remove container after export: podman rm <name>
- Keep tar + meta in workspace for inspection.

Podman/container readiness definition (must be implemented)
When WSL is usable:
- Podman is available inside WSL (verify: podman --version executed inside WSL).
- A fixed image name exists: wsl-sandbox-mcp-agent:latest.
- Image exists or can be built non-interactively from a Dockerfile shipped with the project.
If any step fails → report zero tools.

Bootstrap strategy (must be non-interactive)
- Verify-only first:
  - wsl -e sh -lc "command -v podman"
  - wsl -e sh -lc "podman info"
  - wsl -e sh -lc "podman image exists wsl-sandbox-mcp-agent:latest"
- If Podman missing:
  - Attempt install only if it can be done non-interactively:
    - Use sudo -n and fail fast if it requires a password.
    - Example: sudo -n apt-get update && sudo -n apt-get install -y podman
  - If install fails → not ready → zero tools.
- If image missing:
  - Build from shipped Dockerfile:
    - podman build -t wsl-sandbox-mcp-agent:latest <path>
  - If build fails → not ready → zero tools.

Tool: run_linux_cli
Expose exactly one MCP tool.

Name
run_linux_cli

Input schema (JSON)
- cmd (string, required)
- args (string[], required)
- cwd (string, optional; default ".") relative to workspace root
- timeout_s (int, optional; default 120; clamp 1..3600)
- env (object<string,string>, optional)

Output schema (JSON)
- exit_code (int)
- stdout (string)
- stderr (string)
- artifact_tar (string, optional): relative path to exported filesystem tar, e.g. out/<tool_call_id>.tar
- artifact_meta (string, optional): relative path to meta json, e.g. out/<tool_call_id>.meta.json

Workspace rules (per-user, no cross-user mixing)
- Workspace root on Windows: %USERPROFILE%\.wsl-sandbox-mcp\workspace
- Ensure folders exist:
  - %USERPROFILE%\.wsl-sandbox-mcp\workspace\out
- Map workspace into container at /workspace.
- Reject cwd that attempts traversal (.., absolute paths, drive prefixes, or contains :). 

Container invocation (must support export)
Prefer podman create + podman start -a so export can always happen:
- podman create --name <name> --rm=false -v "<wslPathToWorkspace>:/workspace:rw" -w "/workspace/<cwd>" <image> <cmd> <args...>
- podman start -a <name> (capture stdout/stderr + exit code)
- podman export <name> -o /workspace/out/<tool_call_id>.tar
- podman rm <name>

Timeout handling:
- If timeout hits, stop the container (podman stop -t 1 <name>), attempt export, then remove.

MCP protocol over stdio (implementation requirements)
- Implement a minimal MCP server over stdio:
  - Read JSON messages from stdin.
  - Write JSON responses/events to stdout.
  - Single-threaded message loop with cancellation and safe shutdown.
- Tool list computed at startup and cached for the process lifetime.
- If run_linux_cli called when tool not available, return a standard tool-not-available error.

Project structure
Create a .NET solution:
- WslSandboxMcp.sln
- src/WslSandboxMcp/ (console app)
  - WslSandboxMcp.csproj targeting net8.0 (or net8.0-windows)
  - Program.cs
  - Mcp/
    - StdioTransport.cs
    - McpServer.cs
    - JsonRpcModels.cs
  - Runtime/
    - WslProbe.cs (WSL probes)
    - PodmanBootstrap.cs (includes Mode A env + config generation, and image verify/build)
    - LinuxCliRunner.cs (implements run_linux_cli + Mode B export)
    - PathMapping.cs (Windows ↔ WSL path mapping + cwd sanitization)
    - ProcessExec.cs (spawn processes, timeout, capture stdout/stderr)
  - Container/
    - Dockerfile for wsl-sandbox-mcp-agent:latest (include python3 + pip; minimal)
- README.txt must include:
  - How gating works (0 tools vs 1 tool)
  - Mode A inspection path via \wsl$
  - Mode B tar/meta artifact locations under workspace\out
  - Troubleshooting for missing WSL/distro/podman/sudo -n failures

Security and correctness constraints
- Only execute host commands: wsl.exe plus filesystem operations to create workspace folders.
- Validate/sanitize all inputs (cmd, args, cwd, env).
- Quote/escape safely when constructing sh -lc commands.
- Enforce timeouts and terminate best-effort.
- Tool results must return captured stdout/stderr exactly; no added commentary.

Deliverables
- Full compilable C# project as described.
- Dockerfile for the agent image.
- README.txt with inspection instructions for Mode A and Mode B.
