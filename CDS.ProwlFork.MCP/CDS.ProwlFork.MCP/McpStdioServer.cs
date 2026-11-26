using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace CDS.ProwlFork.MCP;

/// <summary>A minimal MCP server that exchanges one JSON-RPC message per stdio line.</summary>
public static class McpStdioServer
{
    private const string DefaultProtocolVersion = "2024-11-05";

    private static readonly IReadOnlyDictionary<string, object?> EmptyArguments = new Dictionary<string, object?>();
    private static readonly IReadOnlyDictionary<string, object?> EmptySchema = new Dictionary<string, object?>();

    /// <summary>Runs the server until the input reader reaches end of stream.</summary>
    /// <param name="tools">The tool registry served by this server.</param>
    /// <param name="serverName">The name reported in the <c>initialize</c> result.</param>
    /// <param name="version">The version reported in the <c>initialize</c> result.</param>
    /// <param name="input">Input reader; defaults to <see cref="Console.In"/>.</param>
    /// <param name="output">Output writer; defaults to <see cref="Console.Out"/>.</param>
    public static void Run(
        McpToolRegistry tools,
        string serverName,
        string version,
        TextReader? input = null,
        TextWriter? output = null)
    {
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(serverName);
        ArgumentNullException.ThrowIfNull(version);

        TextReader reader = input ?? Console.In;
        TextWriter writer = output ?? Console.Out;

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            string? response = Dispatch(line, tools, serverName, version);
            if (response is null)
            {
                continue;
            }

            writer.WriteLine(response);
            writer.Flush();
        }
    }

    /// <summary>Handles one JSON-RPC line and returns the response line, or null for notifications.</summary>
    public static string? HandleLine(string line, McpToolRegistry tools, string serverName, string version)
        => Dispatch(line, tools, serverName, version);

    internal static string? Dispatch(string line, McpToolRegistry tools, string serverName, string version)
    {
        if (!McpJson.TryParseRequest(line, out McpRequest? request, out JsonElement id, out string? error, out int errorCode))
        {
            return McpJson.SerializeError(id, errorCode, error ?? "Invalid request");
        }

        string? response;
        try
        {
            response = Handle(request!, tools, serverName, version);
        }
        catch (Exception ex)
        {
            response = McpJson.SerializeError(request!.Id, McpJson.InternalError, ex.Message);
        }

        // Notifications have no id and must not be answered.
        return request!.HasId ? response : null;
    }

    private static string Handle(McpRequest request, McpToolRegistry tools, string serverName, string version)
    {
        switch (request.Method)
        {
            case "initialize":
            {
                string protocolVersion = DefaultProtocolVersion;
                if (request.Parameters.TryGetValue("protocolVersion", out object? requested) &&
                    requested is string requestedVersion &&
                    requestedVersion.Length > 0)
                {
                    protocolVersion = requestedVersion;
                }

                object result = new
                {
                    protocolVersion,
                    capabilities = new { tools = new { } },
                    serverInfo = new { name = serverName, version },
                };

                return McpJson.SerializeResult(request.Id, result);
            }

            case "tools/list":
            {
                var listed = new List<object>();
                foreach (McpTool tool in tools.List())
                {
                    listed.Add(new
                    {
                        name = tool.Name,
                        description = tool.Description,
                        inputSchema = (object?)(tool.InputSchema ?? EmptySchema),
                    });
                }

                return McpJson.SerializeResult(request.Id, new { tools = listed });
            }

            case "tools/call":
            {
                if (!request.Parameters.TryGetValue("name", out object? nameValue) || nameValue is not string name)
                {
                    return McpJson.SerializeError(request.Id, McpJson.InvalidParams, "Missing tool name");
                }

                IReadOnlyDictionary<string, object?> arguments = EmptyArguments;
                if (request.Parameters.TryGetValue("arguments", out object? argumentsValue) &&
                    argumentsValue is IReadOnlyDictionary<string, object?> supplied)
                {
                    arguments = supplied;
                }

                tools.TryInvoke(name, arguments, out McpToolResult result);
                return McpJson.SerializeToolResult(request.Id, result);
            }

            case "ping":
                return McpJson.SerializeResult(request.Id, new { });

            default:
                return McpJson.SerializeError(request.Id, McpJson.MethodNotFound, $"Method not found: {request.Method}");
        }
    }
}
