# CDS.ProwlFork.MCP — Practical Guide

## What it is

`CDS.ProwlFork.MCP` is a small, dependency-free MCP (Model Context Protocol) server library for .NET. It speaks **JSON-RPC 2.0 over stdio**, one compact JSON message per line, using only `System.Text.Json`. It knows nothing about any game engine: a host builds an `McpToolRegistry`, registers tools, and calls `McpStdioServer.Run(tools, serverName, version, [input], [output])`. The same code works in Prowl, a standalone console app, or another engine.

Core API: `McpTool`, `McpToolResult`, `ToolHandler`, `McpToolRegistry`, `McpStdioServer`, `McpJson`, `McpRequest`.

Supported methods: `initialize`, `notifications/initialized`, `tools/list`, `tools/call`, `ping`. Notifications get no response; unknown methods return JSON-RPC `-32601`, bad input `-32700`/`-32600`/`-32602`, handler exceptions `-32603`. `initialize` reports protocol version `2024-11-05` by default (it echoes the client's requested version) and advertises only the `tools` capability.

## How the Prowl editor hosts it

The editor registers Prowl-specific tools and exposes them through the built-in CLI command `mcp_serve`. The CLI is a thin authenticated bridge to a **running** editor (it locates it via `<project>/Library/Cli.lock`), so the project must be open in the editor first.

Run it from the project folder (or pass `--project <path>`), with a timeout longer than the client session:

```powershell
# From the project root; the editor must already be open on this project.
.\Library\prowl command mcp_serve --timeout 3600
```

`--timeout` is in seconds (default `120`, max `86400` = 24 hours). A short timeout will end the session mid-conversation. Do **not** pass `--json`: that prints the CLI envelope instead of the raw tool output and breaks the stdio framing. Exit codes: `0` success, `1` error, `130` cancelled (Ctrl+C).

## Configuring an MCP client

Point the client at the CLI command and set the working directory to the project root. Example (`mcpServers` block in Claude Desktop, VS Code, opencode, etc.):

```json
{
  "mcpServers": {
    "prowl": {
      "command": "C:/path/to/project/Library/prowl",
      "args": ["command", "mcp_serve", "--timeout", "3600"],
      "cwd": "C:/path/to/project"
    }
  }
}
```

On Windows, quote paths with spaces. If `cwd` cannot be the project folder, add `"--project", "C:/path/to/project"` to `args`. The client then performs the normal `initialize` → `tools/list` → `tools/call` handshake over stdin/stdout.

## First tool set

The initial set is deliberately a thin bridge over the editor's existing CLI commands, so MCP clients can drive the editor without learning Prowl's internals. The runtime authority is always `tools/list`; the first registration contains:

| Tool | Purpose |
| --- | --- |
| `status` | Current project, open scenes, dirty state, play/pause and compile state, selection, and the log cursor. |
| `logs` | Editor logs, oldest first; pass the `logSeq` from `status` as `--since` to fetch only new lines. |
| `list_commands` | Lists the editor CLI commands with descriptions and argument schemas — the discovery entry point. |
| `run_command` | Runs any editor CLI command by name with positional/`--name value` arguments and returns its JSON result. |
| `eval` | Compiles and runs a C# snippet on the editor main thread; a last-resort escape hatch (no undo). |

`ping` is also available directly as a JSON-RPC method for liveness checks. Tool calls succeed with `content: [{ "type": "text", "text": ... }]` and an `isError` flag; use small, targeted commands (`tree`, `get`, `scene`, `go`, `component`, `asset`, `play`, `screenshot`, `api`) through `run_command` rather than reaching for `eval`.

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
