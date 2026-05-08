using System.Text.Json;

namespace Fetch.Core;

public sealed class ToolSchemaRegistry(IEnumerable<ITool> tools)
{
    private readonly Dictionary<string, ITool> _tools = tools.ToDictionary(t => t.Name, StringComparer.Ordinal);

    public static IReadOnlyList<NativeToolDefinition> BuildDefinitions(IEnumerable<ITool> tools)
    {
        return [.. tools.Select(ToDefinition)];
    }

    public bool TryGetTool(string name, out ITool? tool) => _tools.TryGetValue(name, out tool);

    public static string ConvertArguments(ITool tool, string argumentsJson)
    {
        using JsonDocument doc = JsonDocument.Parse(argumentsJson);
        return ConvertArguments(tool, doc.RootElement);
    }

    public static string ConvertArguments(ITool tool, JsonElement arguments)
    {
        if (tool is INativeTool nativeTool)
        {
            return nativeTool.ConvertArguments(arguments);
        }

        if (arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("input", out JsonElement input))
        {
            return ReadJsonValue(input);
        }

        return ReadJsonValue(arguments);
    }

    private static NativeToolDefinition ToDefinition(ITool tool)
    {
        object parameters = tool is INativeTool nativeTool
            ? nativeTool.GetParametersSchema()
            : new Dictionary<string, object?>
            {
                ["type"] = "object",
                ["properties"] = new Dictionary<string, object?>
                {
                    ["input"] = new Dictionary<string, object?>
                    {
                        ["type"] = "string",
                        ["description"] = "The exact input string for this tool. Follow the tool description precisely."
                    }
                },
                ["required"] = new[] { "input" }
            };

        return new NativeToolDefinition("function", new ToolFunctionDefinition(tool.Name, tool.Description, parameters));
    }

    private static string ReadJsonValue(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.Undefined => "",
            JsonValueKind.Object => value.GetRawText(),
            JsonValueKind.Array => value.GetRawText(),
            JsonValueKind.String => value.GetString() ?? "",
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => value.GetRawText(),
            JsonValueKind.False => value.GetRawText(),
            JsonValueKind.Null => "",
            _ => value.GetRawText()
        };
    }
}
