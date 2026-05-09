using System.Text.Json;

namespace Fetch.Tools;

internal static class NativeToolJson
{
    public static object EmptyObjectSchema() => ObjectSchema([]);

    public static object ObjectSchema(Dictionary<string, object?> properties, params string[] required)
    {
        var schema = new Dictionary<string, object?>
        {
            ["type"] = "object",
            ["properties"] = properties
        };
        if (required.Length > 0)
        {
            schema["required"] = required;
        }

        return schema;
    }

    public static Dictionary<string, object?> StringProperty(string description) => new()
    {
        ["type"] = "string",
        ["description"] = description
    };

    public static Dictionary<string, object?> IntegerProperty(string description, int? minimum = null, int? maximum = null) => new Dictionary<string, object?>
    {
        ["type"] = "integer",
        ["description"] = description,
        ["minimum"] = minimum,
        ["maximum"] = maximum
    }.Where(kvp => kvp.Value is not null).ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

    public static Dictionary<string, object?> BooleanProperty(string description) => new()
    {
        ["type"] = "boolean",
        ["description"] = description
    };

    public static Dictionary<string, object?> ArrayProperty(object items, string description, int? minItems = null) => new Dictionary<string, object?>
    {
        ["type"] = "array",
        ["items"] = items,
        ["description"] = description,
        ["minItems"] = minItems
    }.Where(kvp => kvp.Value is not null).ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

    public static string Serialize(object value) => JsonSerializer.Serialize(value, AgentConfig.JsonOptions());

    public static string SerializeObject(Dictionary<string, object?> values) => Serialize(values.Where(kvp => kvp.Value is not null).ToDictionary(kvp => kvp.Key, kvp => kvp.Value));

    public static bool TryGetString(JsonElement arguments, string propertyName, out string value, bool allowEmpty = false)
    {
        value = "";
        if (arguments.ValueKind != JsonValueKind.Object
            || !arguments.TryGetProperty(propertyName, out JsonElement property)
            || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString() ?? "";
        return allowEmpty || !string.IsNullOrWhiteSpace(value);
    }

    public static bool TryGetInt(JsonElement arguments, string propertyName, out int value)
    {
        value = 0;
        return arguments.ValueKind == JsonValueKind.Object
            && arguments.TryGetProperty(propertyName, out JsonElement property)
            && property.TryGetInt32(out value);
    }

    public static bool TryGetStringArray(JsonElement arguments, string propertyName, out string[] values, bool allowEmpty = false)
    {
        values = [];
        if (arguments.ValueKind != JsonValueKind.Object
            || !arguments.TryGetProperty(propertyName, out JsonElement property)
            || property.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var items = new List<string>();
        foreach (JsonElement item in property.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            var text = item.GetString() ?? "";
            if (!allowEmpty && string.IsNullOrWhiteSpace(text))
            {
                return false;
            }
            items.Add(text);
        }

        if (!allowEmpty && items.Count == 0)
        {
            return false;
        }

        values = [.. items];
        return true;
    }

    public static bool TryGetElement(JsonElement arguments, string propertyName, out JsonElement value)
    {
        value = default;
        return arguments.ValueKind == JsonValueKind.Object
            && arguments.TryGetProperty(propertyName, out value);
    }
}