# CDS.ProwlFork.MCP

A minimal, dependency-free MCP (Model Context Protocol) server for .NET. It speaks
JSON-RPC 2.0 over stdio, one message per line, and has no engine or third-party
dependencies: just `System.Text.Json` from the base class library.

## Supported messages

| Method | Behaviour |
| --- | --- |
| `initialize` | Returns protocol version, `tools` capability, and server name/version. |
| `notifications/initialized` | Acknowledged silently (notifications get no response). |
| `tools/list` | Returns registered tools with their descriptions and input schemas. |
| `tools/call` | Invokes a registered tool and returns `content: [{ type: "text", text: ... }]`. |
| `ping` | Returns an empty result. |
| anything else | JSON-RPC error `-32601` with the request id preserved. |

Invalid JSON, malformed requests, and handler exceptions are reported as JSON-RPC
errors (`-32700`, `-32600`/`-32602`, `-32603`). Responses are compact single-line
JSON; string, number, and null ids are echoed back unchanged.

## Quick start

```csharp
using CDS.ProwlFork.MCP;

var tools = new McpToolRegistry();

tools.Register(
    new McpTool("echo", "Echoes the supplied text", new Dictionary<string, object?>
    {
        ["type"] = "object",
        ["properties"] = new Dictionary<string, object?>
        {
            ["text"] = new Dictionary<string, object?> { ["type"] = "string" },
        },
    }),
    args => new McpToolResult(false, args.TryGetValue("text", out var text) ? text?.ToString() ?? "" : ""));

McpStdioServer.Run(tools, "my-server", "1.0.0");
```

For tests or host integration, pass your own `TextReader`/`TextWriter` to `Run`.

## API

- `McpTool`, `McpToolResult`, `ToolHandler`
- `McpToolRegistry` — `Register`, `List`, `TryInvoke`
- `McpStdioServer.Run` — the stdio message loop
- `McpJson` — line parsing and response serialization helpers
- `McpRequest` — a parsed JSON-RPC request

## License

MIT, as per the Anthology repository license.
