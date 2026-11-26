# CDS.ProwlFork.MCP — Practical Guide

## What it is

`CDS.ProwlFork.MCP` is a small, dependency-free MCP (Model Context Protocol) server library for .NET. It speaks **JSON-RPC 2.0 over stdio**, one compact JSON message per line, using only `System.Text.Json`. It knows nothing about any game engine: a host builds an `McpToolRegistry`, registers tools, and calls `McpStdioServer.Run(tools, serverName, version, [input], [output])`. The same code works in Prowl, a standalone console app, or another engine.

Core API: `McpTool`, `McpToolResult`, `ToolHandler`, `McpToolRegistry`, `McpStdioServer`, `McpJson`, `McpRequest`.

Supported methods: `initialize`, `notifications/initialized`, `tools/list`, `tools/call`, `ping`. Notifications get no response; unknown methods return JSON-RPC `-32601`, bad input `-32700`/`-32600`/`-32602`, handler exceptions `-32603`. `initialize` reports protocol version `2024-11-05` by default (it echoes the client's requested version) and advertises only the `tools` capability.

## How the Prowl editor hosts it

The editor registers 15 Prowl-specific tools in `Prowl.Editor/MCP/` and exposes them through the CLI `mcp` verb. The reason for a dedicated verb: CLI commands travel over a loopback socket to the editor process, so piping stdin into `prowl command mcp_serve` never reaches the editor's `Console.In`. Instead, `Library/prowl mcp` reads one JSON-RPC message per stdin line in the CLI process and forwards each line to the editor's `mcp_dispatch` command; each response line is written back to stdout. Notifications produce no output, as the protocol requires.

The CLI locates a **running** editor via `<project>/Library/Cli.lock`, so the project must be open in the editor first. Run from the project folder (or pass `--project <path>`):

```powershell
# From the project root; the editor must already be open on this project.
.\Library\prowl mcp --timeout 3600
```

`--timeout` is in seconds (default `120`, max `86400` = 24 hours) and applies per dispatched call. Do **not** pass `--json`. Exit codes: `0` success, `1` error, `130` cancelled (Ctrl+C). The editor-side `mcp_serve` command is still available for direct/in-editor use, but it is not usable through CLI stdin piping.

A leading UTF-8 BOM on the first line (e.g. from a PowerShell pipe) is tolerated; real MCP clients normally omit it.

## Configuring an MCP client

Point the client at the CLI command with the `mcp` verb and set the working directory to the project root. Example (`mcpServers` block in Claude Desktop, VS Code, opencode, etc.):

```json
{
  "mcpServers": {
    "prowl": {
      "command": "C:/path/to/project/Library/prowl",
      "args": ["mcp", "--timeout", "3600"],
      "cwd": "C:/path/to/project"
    }
  }
}
```

On Windows, quote paths with spaces. If `cwd` cannot be the project folder, add `"--project", "C:/path/to/project"` to `args`. The client then performs the normal `initialize` → `tools/list` → `tools/call` handshake over stdin/stdout.

## First tool set

The initial 15 tools are a thin bridge over the editor's existing CLI commands plus direct scene-view control, so MCP clients can drive the editor without learning Prowl's internals. The runtime authority is always `tools/list`; the current registration contains:

| Tool | Purpose |
| --- | --- |
| `prowl_status` | Current project, open scenes, dirty state, play/pause and compile state, selection, and the log cursor. Maps to CLI `status`. |
| `scene_list` / `scene_open` | List open scenes or describe one, and open a scene from an Assets-relative path. |
| `tree` / `select` | Inspect the hierarchy (with optional root/depth/filter) and select refs, optionally framing them. |
| `get` / `set` | Read serialized fields or a field path; write field paths from JSON in one undo step. |
| `component_add` / `go_create` | Add a component to a GameObject; create a GameObject. |
| `camera_frame` | Select and frame a target in the scene view (CLI `select --frame`). |
| `camera_projection` | Query or toggle perspective/orthographic on the scene camera. |
| `scene_wireframe` / `scene_grid` / `scene_gizmos` | Toggle the scene-view wireframe, grid, and gizmo overlays. |
| `lod_bias` | Read or set the LOD bias used by scene rendering. |

`ping` is also available directly as a JSON-RPC method for liveness checks. Tool calls succeed with `content: [{ "type": "text", "text": ... }]` and an `isError` flag; results are the underlying CLI JSON as text.

## Safety notes

- Every CLI command — and therefore every MCP tool call — runs on the **editor main thread**, with logs captured around it. A long or blocking call freezes the editor UI and stalls all other CLI/MCP calls; a modal dialog can prevent dispatch entirely.
- Keep calls short and incremental. Avoid sleeps, long builds/wait loops, or arbitrary waiting inside `eval`/`run_command`; prefer commands that return immediately and poll `status`/`logs`.
- Launch the client from a **real terminal** so you can see editor output and cancel with Ctrl+C. Do not bury a long-lived session in a background UI panel where it can be orphaned.
- The CLI holds an editor lock file. If the editor is closed or the project changes, pending calls fail; stop the client and restart it against the reopened project.
- Tools can modify scenes and assets. Prefer commands that create a single undo step; save/check dirty state before bulk edits.

## Limitations

- Protocol subset only: no resources, prompts, sampling, progress, cancellation, pagination, or MCP HTTP/SSE transport. Stdio is line-delimited JSON only; no batch JSON-RPC requests.
- Results are text only — no images, audio, or structured content. `isError` carries an error string, not a typed error object.
- `initialize` does not negotiate capabilities beyond echoing the requested version; assume `2024-11-05` behavior.
- The CLI bridge is request/response with a hard timeout: sessions are not resumable, and a timed-out command may still be running in the editor.
- The library itself has no authentication or engine knowledge; access control and all editor semantics live in the hosting application. Do not expose the client to untrusted input.
