using System.Collections.Generic;
using System.Text.Json;

namespace CDS.ProwlFork.MCP;

/// <summary>A parsed JSON-RPC 2.0 request.</summary>
public sealed class McpRequest
{
    /// <summary>Creates a parsed request. Used by <see cref="McpJson"/>.</summary>
    public McpRequest(string method, JsonElement id, IReadOnlyDictionary<string, object?>? parameters = null)
    {
        Method = method;
        Id = id;
        Parameters = parameters ?? EmptyParameters;
    }

    /// <summary>The JSON-RPC method name, for example <c>tools/list</c>.</summary>
    public string Method { get; }

    /// <summary>The request id, echoed back in the response. Undefined for notifications.</summary>
    public JsonElement Id { get; }

    /// <summary>The parsed <c>params</c> object, or an empty dictionary.</summary>
    public IReadOnlyDictionary<string, object?> Parameters { get; }

    /// <summary>Whether this message carries an id and therefore expects a response.</summary>
    public bool HasId => Id.ValueKind != JsonValueKind.Undefined;

    private static readonly IReadOnlyDictionary<string, object?> EmptyParameters = new Dictionary<string, object?>();
}
