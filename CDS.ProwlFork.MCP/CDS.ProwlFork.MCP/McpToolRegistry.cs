using System;
using System.Collections.Generic;

namespace CDS.ProwlFork.MCP;

/// <summary>An ordered, case-sensitive registry of MCP tools and their handlers.</summary>
public sealed class McpToolRegistry
{
    private readonly Dictionary<string, ToolEntry> _entries = new(StringComparer.Ordinal);
    private readonly List<string> _order = new();

    /// <summary>Registers a tool, replacing any previous tool with the same name.</summary>
    public void Register(McpTool tool, ToolHandler handler)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(handler);

        if (string.IsNullOrWhiteSpace(tool.Name))
        {
            throw new ArgumentException("A tool name cannot be empty.", nameof(tool));
        }

        if (!_entries.ContainsKey(tool.Name))
        {
            _order.Add(tool.Name);
        }

        _entries[tool.Name] = new ToolEntry(tool, handler);
    }

    /// <summary>Lists the registered tools in registration order.</summary>
    public IReadOnlyList<McpTool> List()
    {
        var tools = new McpTool[_order.Count];
        for (int i = 0; i < _order.Count; i++)
        {
            tools[i] = _entries[_order[i]].Tool;
        }

        return tools;
    }

    /// <summary>Invokes a tool by name. Returns false and an error result if the tool is unknown.</summary>
    public bool TryInvoke(string name, IReadOnlyDictionary<string, object?> args, out McpToolResult result)
    {
        if (name is null || !_entries.TryGetValue(name, out ToolEntry? entry) || entry is null)
        {
            result = new McpToolResult(true, $"Unknown tool: {name}");
            return false;
        }

        result = entry.Handler(args ?? EmptyArguments);
        return true;
    }

    private sealed record ToolEntry(McpTool Tool, ToolHandler Handler);

    private static readonly IReadOnlyDictionary<string, object?> EmptyArguments = new Dictionary<string, object?>();
}
