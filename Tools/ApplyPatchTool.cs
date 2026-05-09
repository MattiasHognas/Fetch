using System.Text.Json;

namespace Fetch.Tools;

public sealed class ApplyPatchTool(PathSandbox sandbox, SecretPolicy secrets) : ITool, INativeTool
{
    private readonly PathSandbox _sandbox = sandbox;
    private readonly SecretPolicy _secrets = secrets;

    public string Name => "apply_patch";
    public string Description => "Apply simple text replacement from JSON arguments.";
    public ApprovalMode Approval => ApprovalMode.Ask;

    public object GetParametersSchema() => NativeToolJson.ObjectSchema(new Dictionary<string, object?>
    {
        ["path"] = NativeToolJson.StringProperty("Repo-relative path of the file to edit."),
        ["oldText"] = NativeToolJson.StringProperty("Exact current text to replace."),
        ["newText"] = NativeToolJson.StringProperty("Replacement text.")
    }, "path", "oldText", "newText");

    public string ConvertArguments(JsonElement arguments)
    {
        return NativeToolJson.TryGetString(arguments, "path", out var path)
            && NativeToolJson.TryGetString(arguments, "oldText", out var oldText, allowEmpty: true)
            && NativeToolJson.TryGetString(arguments, "newText", out var newText, allowEmpty: true)
            ? NativeToolJson.SerializeObject(new Dictionary<string, object?>
            {
                ["path"] = path,
                ["oldText"] = oldText,
                ["newText"] = newText
            })
            : "";
    }

    public async Task<string> RunAsync(string input)
    {
        if (!TryParseInput(input, out var rawPath, out var oldText, out var newText))
        {
            return "Invalid input. Provide JSON {\"path\":\"file\",\"oldText\":\"...\",\"newText\":\"...\"}.";
        }

        var path = _sandbox.Resolve(rawPath);
        _secrets.ThrowIfSensitive(path);
        if (!File.Exists(path))
        {
            return $"File not found: {rawPath}";
        }

        var c = await File.ReadAllTextAsync(path);
        if (!c.Contains(oldText, StringComparison.Ordinal))
        {
            return "Old text not found. Patch failed.";
        }

        await File.WriteAllTextAsync(path, c.Replace(oldText, newText, StringComparison.Ordinal));
        return "Patch applied successfully.";
    }

    private static bool TryParseInput(string input, out string path, out string oldText, out string newText)
    {
        path = "";
        oldText = "";
        newText = "";
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(input);
            return NativeToolJson.TryGetString(doc.RootElement, "path", out path)
                && NativeToolJson.TryGetString(doc.RootElement, "oldText", out oldText, allowEmpty: true)
                && NativeToolJson.TryGetString(doc.RootElement, "newText", out newText, allowEmpty: true);
        }
        catch
        {
            return false;
        }
    }
}
