using System.Collections.Generic;

namespace CDS.ProwlFork.MCP;

/// <summary>Describes a tool that an MCP server exposes to clients.</summary>
/// <param name="Name">The tool name used by <c>tools/call</c>.</param>
/// <param name="Description">A human-readable description of what the tool does.</param>
/// <param name="InputSchema">An optional JSON Schema describing the tool arguments.</param>
public sealed record McpTool(string Name, string Description, IReadOnlyDictionary<string, object?>? InputSchema = null);

/// <summary>The result of a tool invocation.</summary>
/// <param name="IsError">Whether the invocation failed.</param>
/// <param name="Content">The textual content returned to the MCP client.</param>
public sealed record McpToolResult(bool IsError, string Content);

/// <summary>Handles a tool invocation, given the arguments supplied by the client.</summary>
/// <param name="arguments">The parsed <c>arguments</c> object from a <c>tools/call</c> request.</param>
/// <returns>The result to send back to the client.</returns>
public delegate McpToolResult ToolHandler(IReadOnlyDictionary<string, object?> arguments);
