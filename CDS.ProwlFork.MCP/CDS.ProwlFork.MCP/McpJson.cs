using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace CDS.ProwlFork.MCP;

/// <summary>Parsing and serialization helpers for MCP JSON-RPC 2.0 messages.</summary>
public static class McpJson
{
    public const int ParseError = -32700;
    public const int InvalidRequest = -32600;
    public const int MethodNotFound = -32601;
    public const int InvalidParams = -32602;
    public const int InternalError = -32603;

    private static readonly IReadOnlyDictionary<string, object?> EmptyParameters = new Dictionary<string, object?>();

    /// <summary>Parses one line of JSON-RPC into a request.</summary>
    public static bool TryParseRequest(string line, out McpRequest? request, out string? error)
    {
        bool parsed = TryParseRequest(line, out request, out _, out string? parseError, out _);
        error = parseError;
        return parsed;
    }

    /// <summary>Parses one line of JSON-RPC, also reporting the id and the JSON-RPC error code.</summary>
    public static bool TryParseRequest(
        string line,
        out McpRequest? request,
        out JsonElement id,
        out string? error,
        out int errorCode)
    {
        request = null;
        id = default;
        error = null;
        errorCode = 0;

        if (string.IsNullOrWhiteSpace(line))
        {
            error = "Empty input";
            errorCode = ParseError;
            return false;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException ex)
        {
            error = $"Parse error: {ex.Message}";
            errorCode = ParseError;
            return false;
        }

        using (document)
        {
            JsonElement root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "A JSON-RPC request must be an object";
                errorCode = InvalidRequest;
                return false;
            }

            if (root.TryGetProperty("id", out JsonElement idElement))
            {
                id = idElement.Clone();
            }

            if (!root.TryGetProperty("jsonrpc", out JsonElement version) ||
                version.ValueKind != JsonValueKind.String ||
                version.GetString() != "2.0")
            {
                error = "jsonrpc must be \"2.0\"";
                errorCode = InvalidRequest;
                return false;
            }

            if (!root.TryGetProperty("method", out JsonElement methodElement) ||
                methodElement.ValueKind != JsonValueKind.String)
            {
                error = "method must be a string";
                errorCode = InvalidRequest;
                return false;
            }

            IReadOnlyDictionary<string, object?> parameters = EmptyParameters;
            if (root.TryGetProperty("params", out JsonElement paramsElement) &&
                paramsElement.ValueKind != JsonValueKind.Null)
            {
                if (paramsElement.ValueKind != JsonValueKind.Object)
                {
                    error = "params must be an object";
                    errorCode = InvalidParams;
                    return false;
                }

                parameters = ConvertObject(paramsElement);
            }

            request = new McpRequest(methodElement.GetString()!, id, parameters);
            return true;
        }
    }

    /// <summary>Serializes a successful JSON-RPC response with the given id.</summary>
    public static string SerializeResult(JsonElement id, object result)
    {
        return WriteEnvelope(id, writer =>
        {
            writer.WritePropertyName("result");
            JsonSerializer.Serialize(writer, result);
        });
    }

    /// <summary>Serializes a JSON-RPC error response with the given id.</summary>
    public static string SerializeError(JsonElement id, int code, string message, object? data = null)
    {
        return WriteEnvelope(id, writer =>
        {
            writer.WritePropertyName("error");
            writer.WriteStartObject();
            writer.WriteNumber("code", code);
            writer.WriteString("message", message ?? string.Empty);
            if (data is not null)
            {
                writer.WritePropertyName("data");
                JsonSerializer.Serialize(writer, data);
            }

            writer.WriteEndObject();
        });
    }

    /// <summary>Serializes a tool result as a compact MCP content array with an isError flag.</summary>
    public static string SerializeToolResult(JsonElement id, McpToolResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return WriteEnvelope(id, writer =>
        {
            writer.WritePropertyName("result");
            writer.WriteStartObject();
            writer.WritePropertyName("content");
            writer.WriteStartArray();
            writer.WriteStartObject();
            writer.WriteString("type", "text");
            writer.WriteString("text", result.Content ?? string.Empty);
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteBoolean("isError", result.IsError);
            writer.WriteEndObject();
        });
    }

    private static string WriteEnvelope(JsonElement id, Action<Utf8JsonWriter> writeBody)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            writer.WritePropertyName("id");
            if (id.ValueKind == JsonValueKind.Undefined)
            {
                writer.WriteNullValue();
            }
            else
            {
                id.WriteTo(writer);
            }

            writeBody(writer);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static IReadOnlyDictionary<string, object?> ConvertObject(JsonElement element)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
        {
            result[property.Name] = ConvertValue(property.Value);
        }

        return result;
    }

    private static object? ConvertValue(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                return element.GetString();
            case JsonValueKind.Number:
                if (element.TryGetInt64(out long integer))
                {
                    return integer;
                }

                if (element.TryGetDouble(out double number))
                {
                    return number;
                }

                return element.GetRawText();
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            case JsonValueKind.Object:
                return ConvertObject(element);
            case JsonValueKind.Array:
            {
                var items = new List<object?>();
                foreach (JsonElement item in element.EnumerateArray())
                {
                    items.Add(ConvertValue(item));
                }

                return items;
            }
            default:
                return null;
        }
    }
}
