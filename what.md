1| Prompt: Build “Wsl sandbox mcp” (C# MCP server over stdio; WSL-gated; 0 tools or 1 tool; inspectable container state)
2| 
3| Goal
4| Create a Windows-only MCP server executable named Wsl sandbox mcp implemented in C#/.NET that communicates via stdio (stdin/stdout). The server must expose no tools unless Windows Subsystem for Linux (WSL) is available and usable. In the “WSL usable” case, the server must ensure a Podman-based Linux container execution environment inside WSL and expose exactly one tool: run_linux_cli.
5| 
6| Hard rules
7| 1) If WSL is not usable → MCP server must exit immediately (process terminates with a non-zero exit code) instead of reporting any tools.
8| 2) If WSL is usable and Podman/container environment is ready → MCP server reports exactly one tool: run_linux_cli.
9| 3) If any prerequisite for the “usable” path fails → the MCP server must exit immediately (non-zero exit).
10| 4) No other tools and no diagnostic tools. Only run_linux_cli or nothing.
11| 5) The system must support post-run inspection of what got installed/changed inside the Linux environment.
12| 
13| WSL usability definition (must be implemented)
14| WSL is “usable” only if:
15| - wsl.exe is callable (wsl --version when available).
16| - At least one distro exists (wsl -l -v returns at least one entry).
17| - A trivial command succeeds: wsl -e sh -lc "echo ok" exits 0.
18| If any probe fails → WSL not usable (the MCP server should exit immediately).
19| 
20| Inspectable container state (must be implemented)
21| Support BOTH inspection modes:
22| 
23| Mode A: Stable Podman storage path inside WSL (inspect via \wsl$)
24| - Configure Podman to use a stable per-user storage directory inside WSL:
25|   - WSL path: /home/<linuxuser>/.wsl-sandbox-mcp/podman
26| - Implement by generating a storage.conf file and forcing Podman to use it for all invocations:
27|   - Set CONTAINERS_STORAGE_CONF=/home/<linuxuser>/.wsl-sandbox-mcp/storage.conf
28|   - In storage.conf, set:
29|     - graphroot = "/home/<linuxuser>/.wsl-sandbox-mcp/podman/graphroot"
30|     - runroot  = "/home/<linuxuser>/.wsl-sandbox-mcp/podman/runroot"
31| - Ensure the MCP server applies this env var to every Podman call (bootstrap + tool execution).
32| - README must document how to inspect from Windows using \wsl$\<DistroName>\home\<linuxuser>\.wsl-sandbox-mcp\podman.
33| 
34| Mode B: Export full container filesystem after each run (artifact)
35| - For each run_linux_cli call:
36|   - Use deterministic container name: wsl-sandbox-mcp-<tool_call_id>
37|   - After command finishes (success or failure), export filesystem:
38|     - podman export <name> -o /workspace/out/<tool_call_id>.tar
39|   - Write manifest:
40|     - /workspace/out/<tool_call_id>.meta.json containing { image, cmd, args, cwd, env, exit_code, started_ts, finished_ts }
41|   - Always remove container after export: podman rm <name>
42| - Keep tar + meta in workspace for inspection.
43| 
44| Podman/container readiness definition (must be implemented)
45| When WSL is usable:
46| - Podman is available inside WSL (verify: podman --version executed inside WSL).
47| - A fixed image name exists: wsl-sandbox-mcp-agent:latest.
48| - Image exists or can be built non-interactively from a Dockerfile shipped with the project.
49| If any step fails → the MCP server must exit immediately (non-zero exit).
50| 
51| Bootstrap strategy (must be non-interactive)
52| - Verify-only first:
53|   - wsl -e sh -lc "command -v podman"
54|   - wsl -e sh -lc "podman info"
55|   - wsl -e sh -lc "podman image exists wsl-sandbox-mcp-agent:latest"
56| - If Podman missing:
57|   - Attempt install only if it can be done non-interactively:
58|     - Use sudo -n and fail fast if it requires a password.
59|     - Example: sudo -n apt-get update && sudo -n apt-get install -y podman
60|   - If install fails → not ready → the MCP server must exit immediately (non-zero exit).
61| - If image missing:
62|   - Build from shipped Dockerfile:
63|     - podman build -t wsl-sandbox-mcp-agent:latest <path>
64|   - If build fails → not ready → the MCP server must exit immediately (non-zero exit).
65| 
66| Tool: run_linux_cli
67| Expose exactly one MCP tool.
68| 
69| Name
70| run_linux_cli
71| 
72| Input schema (JSON)
73| - cmd (string, required)
74| - args (string[], required)
75| - cwd (string, optional; default ".") relative to workspace root
76| - timeout_s (int, optional; default 120; clamp 1..3600)
77| - env (object<string,string>, optional)
78| 
79| Output schema (JSON)
80| - exit_code (int)
81| - stdout (string)
82| - stderr (string)
83| - artifact_tar (string, optional): relative path to exported filesystem tar, e.g. out/<tool_call_id>.tar
84| - artifact_meta (string, optional): relative path to meta json, e.g. out/<tool_call_id>.meta.json
85| 
86| Workspace rules (per-user, no cross-user mixing)
87| - Workspace root on Windows: %USERPROFILE%\.wsl-sandbox-mcp\workspace
88| - Ensure folders exist:
89|   - %USERPROFILE%\.wsl-sandbox-mcp\workspace\out
90| - Map workspace into container at /workspace.
91| - Reject cwd that attempts traversal (.., absolute paths, drive prefixes, or contains :) . 
92| 
93| Container invocation (must support export)
94| Prefer podman create + podman start -a so export can always happen:
95| - podman create --name <name> --rm=false -v "<wslPathToWorkspace>:/workspace:rw" -w "/workspace/<cwd>" <image> <cmd> <args...>
96| - podman start -a <name> (capture stdout/stderr + exit code)
97| - podman export <name> -o /workspace/out/<tool_call_id>.tar
98| - podman rm <name>
99| 
100| Timeout handling:
101| - If timeout hits, stop the container (podman stop -t 1 <name>), attempt export, then remove.
102| 
103| MCP protocol over stdio (implementation requirements)
104| - Implement a minimal MCP server over stdio:
105|   - Read JSON messages from stdin.
106|   - Write JSON responses/events to stdout.
107|   - Single-threaded message loop with cancellation and safe shutdown.
108| - Tool list computed at startup and cached for the process lifetime.
109| - If run_linux_cli called when tool not available, return a standard tool-not-available error.
110| 
111| Project structure
112| Create a .NET solution:
113| - WslSandboxMcp.sln
114| - src/WslSandboxMcp/ (console app)
115|   - WslSandboxMcp.csproj targeting net8.0 (or net8.0-windows)
116|   - Program.cs
117|   - Mcp/
118|     - StdioTransport.cs
119|     - McpServer.cs
120|     - JsonRpcModels.cs
121|   - Runtime/
122|     - WslProbe.cs (WSL probes)
123|     - PodmanBootstrap.cs (includes Mode A env + config generation, and image verify/build)
124|     - LinuxCliRunner.cs (implements run_linux_cli + Mode B export)
125|     - PathMapping.cs (Windows ↔ WSL path mapping + cwd sanitization)
126|     - ProcessExec.cs (spawn processes, timeout, capture stdout/stderr)
127|   - Container/
128|     - Dockerfile for wsl-sandbox-mcp-agent:latest (include python3 + pip; minimal)
129| - README.txt must include:
130|   - How gating works (0 tools vs 1 tool)
131|   - Mode A inspection path via \wsl$
132|   - Mode B tar/meta artifact locations under workspace\out
133|   - Troubleshooting for missing WSL/distro/podman/sudo -n failures
134| 
135| Security and correctness constraints
136| - Only execute host commands: wsl.exe plus filesystem operations to create workspace folders.
137| - Validate/sanitize all inputs (cmd, args, cwd, env).
138| - Quote/escape safely when constructing sh -lc commands.
139| - Enforce timeouts and terminate best-effort.
140| - Tool results must return captured stdout/stderr exactly; no added commentary.
141| 
142| Deliverables
143| - Full compilable C# project as described.
144| - Dockerfile for the agent image.
145| - README.txt with inspection instructions for Mode A and Mode B.
146| 
147| 
148| 
149| Example of Program.exe that exits if wsl is not available:
150| 
151| using Microsoft.Extensions.DependencyInjection;
152| using Microsoft.Extensions.Hosting;
153| using Microsoft.Extensions.Logging;
154| 
155| public class Program
156| {
157|     public static async Task Main(string[] args)
158|     {
159|         if (!HasWsl())
160|             Environment.Exit(1);
161| 
162|         var builder = Host.CreateApplicationBuilder(args);
163|         builder.Logging.AddConsole(consoleLogOptions =>
164|         {
165|             // Configure all logs to go to stderr
166|             consoleLogOptions.LogToStandardErrorThreshold = Microsoft.Extensions.Logging.LogLevel.Trace;
167|         });
168| 
169|         //var runtimeTools = await GraphServer.ToolsFromClass.GetToolsAsync().ConfigureAwait(false);
170| 
171|         builder.Services
172|             .AddMcpServer()
173|             .WithStdioServerTransport()
174|             .WithToolsFromAssembly();
175|             //.WithTools(runtimeTools);
176| 
177|         await builder.Build().RunAsync();
178|     }
179| }