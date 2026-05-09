using System.Text.Json;

namespace Fetch.Tools;

public sealed class ReadFileTool(FileReadRegistry registry, PathSandbox sandbox, SecretPolicy secrets) : ITool, INativeTool
{
    private readonly FileReadRegistry _registry = registry;
    private readonly PathSandbox _sandbox = sandbox;
    private readonly SecretPolicy _secrets = secrets;

    public string Name => "read_file";
    public string Description => "Read a file by path.";
    public ApprovalMode Approval => ApprovalMode.Auto;

    public object GetParametersSchema() => FileToolArguments.RequiredPathSchema("Repo-relative path of the file to read.");

    public string ConvertArguments(JsonElement arguments) => FileToolArguments.ConvertPathArguments(arguments);

    public async Task<string> RunAsync(string input)
    {
        if (!FileToolArguments.TryParsePathInput(input, out var rawPath))
        {
            return "Invalid input. Provide JSON {\"path\":\"path/to/file\"}.";
        }

        var path = _sandbox.Resolve(rawPath);
        _secrets.ThrowIfSensitive(path);
        if (!File.Exists(path))
        {
            return $"File not found: {rawPath}";
        }

        var c = await File.ReadAllTextAsync(path);
        _registry.MarkRead(path, c);
        return c;
    }
}

public sealed class GetFileSummaryTool(FileReadRegistry registry, PathSandbox sandbox, SecretPolicy secrets) : ITool, INativeTool
{
    private readonly FileReadRegistry _registry = registry;
    private readonly PathSandbox _sandbox = sandbox;
    private readonly SecretPolicy _secrets = secrets;

    public string Name => "read_head";
    public string Description => "Read the first 100 lines of a file.";
    public ApprovalMode Approval => ApprovalMode.Auto;

    public object GetParametersSchema() => FileToolArguments.RequiredPathSchema("Repo-relative path of the file to summarize.");

    public string ConvertArguments(JsonElement arguments) => FileToolArguments.ConvertPathArguments(arguments);

    public async Task<string> RunAsync(string input)
    {
        if (!FileToolArguments.TryParsePathInput(input, out var rawPath))
        {
            return "Invalid input. Provide JSON {\"path\":\"path/to/file\"}.";
        }

        var path = _sandbox.Resolve(rawPath);
        _secrets.ThrowIfSensitive(path);
        if (!File.Exists(path))
        {
            return $"Not found: {rawPath}";
        }

        var c = await File.ReadAllTextAsync(path);
        _registry.MarkRead(path, c);
        return string.Join("\n", c.Replace("\r\n", "\n").Split('\n').Take(100));
    }
}

public sealed class ListFilesTool(PathSandbox sandbox) : ITool, INativeTool
{
    private readonly PathSandbox _sandbox = sandbox;

    public string Name => "list_files";
    public string Description => "List files in a directory.";
    public ApprovalMode Approval => ApprovalMode.Auto;

    public object GetParametersSchema() => FileToolArguments.OptionalPathSchema("Optional repo-relative directory to list. Defaults to the workspace root.");

    public string ConvertArguments(JsonElement arguments) => FileToolArguments.ConvertOptionalPathArguments(arguments);

    public Task<string> RunAsync(string input)
    {
        if (!FileToolArguments.TryParseOptionalPathInput(input, out var rawPath))
        {
            return Task.FromResult("Invalid input. Provide JSON {\"path\":\"path/to/dir\"} or omit path.");
        }

        var p = _sandbox.Resolve(rawPath);
        return !Directory.Exists(p)
        ? Task.FromResult($"Directory not found: {rawPath}")
        : Task.FromResult(string.Join("\n", Directory.GetFiles(p).Select(_sandbox.Relative)));
    }
}

public sealed class CreateFileTool(PathSandbox sandbox, SecretPolicy secrets) : ITool, INativeTool
{
    private readonly PathSandbox _sandbox = sandbox; private readonly SecretPolicy _secrets = secrets;

    public string Name => "create_file";
    public string Description => "Create a file from JSON arguments.";
    public ApprovalMode Approval => ApprovalMode.Ask;

    public object GetParametersSchema() => FileToolArguments.CreateFileSchema();

    public string ConvertArguments(JsonElement arguments) => FileToolArguments.ConvertCreateFileArguments(arguments);

    public async Task<string> RunAsync(string input)
    {
        if (!FileToolArguments.TryParseCreateFileInput(input, out var rawPath, out var content))
        {
            return "Invalid input. Provide JSON {\"path\":\"docs/file.md\",\"content\":\"...\"}.";
        }

        var path = _sandbox.Resolve(rawPath.Trim());
        _secrets.ThrowIfSensitive(path);
        if (File.Exists(path))
        {
            return $"File already exists: {rawPath}. Use apply_diff with '*** Update File: {rawPath}' to modify it.";
        }

        _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content);
        return $"Created file: {rawPath}";
    }
}

public sealed class DeleteFileTool(PathSandbox sandbox, SecretPolicy secrets) : ITool, INativeTool
{
    private readonly PathSandbox _sandbox = sandbox; private readonly SecretPolicy _secrets = secrets;

    public string Name => "delete_file";
    public string Description => "Delete a file by path.";
    public ApprovalMode Approval => ApprovalMode.Ask;

    public object GetParametersSchema() => FileToolArguments.RequiredPathSchema("Repo-relative path of the file to delete.");

    public string ConvertArguments(JsonElement arguments) => FileToolArguments.ConvertPathArguments(arguments);

    public Task<string> RunAsync(string input)
    {
        if (!FileToolArguments.TryParsePathInput(input, out var rawPath))
        {
            return Task.FromResult("Invalid input. Provide JSON {\"path\":\"path/to/file\"}.");
        }

        var path = _sandbox.Resolve(rawPath);
        _secrets.ThrowIfSensitive(path);
        if (!File.Exists(path))
        {
            return Task.FromResult($"File not found: {rawPath}");
        }

        File.Delete(path);
        return Task.FromResult($"Deleted file: {rawPath}");
    }
}

public sealed class RenameFileTool(PathSandbox sandbox, SecretPolicy secrets) : ITool, INativeTool
{
    private readonly PathSandbox _sandbox = sandbox; private readonly SecretPolicy _secrets = secrets;

    public string Name => "rename_file";
    public string Description => "Rename a file from JSON arguments.";
    public ApprovalMode Approval => ApprovalMode.Ask;

    public object GetParametersSchema() => FileToolArguments.RenameFileSchema();

    public string ConvertArguments(JsonElement arguments) => FileToolArguments.ConvertRenameFileArguments(arguments);

    public Task<string> RunAsync(string input)
    {
        if (!FileToolArguments.TryParseRenameFileInput(input, out var oldPath, out var newPath))
        {
            return Task.FromResult("Invalid input. Provide JSON {\"oldPath\":\"old\",\"newPath\":\"new\"}.");
        }

        var oldp = _sandbox.Resolve(oldPath);
        var newp = _sandbox.Resolve(newPath);
        _secrets.ThrowIfSensitive(oldp);
        _secrets.ThrowIfSensitive(newp);
        if (!File.Exists(oldp))
        {
            return Task.FromResult($"Not found: {oldPath}");
        }

        _ = Directory.CreateDirectory(Path.GetDirectoryName(newp)!);
        File.Move(oldp, newp);
        return Task.FromResult($"Renamed to: {newPath}");
    }
}

internal static class FileToolArguments
{
    public static object RequiredPathSchema(string description) => new Dictionary<string, object?>
    {
        ["type"] = "object",
        ["properties"] = new Dictionary<string, object?>
        {
            ["path"] = new Dictionary<string, object?>
            {
                ["type"] = "string",
                ["description"] = description
            }
        },
        ["required"] = new[] { "path" }
    };

    public static object OptionalPathSchema(string description) => new Dictionary<string, object?>
    {
        ["type"] = "object",
        ["properties"] = new Dictionary<string, object?>
        {
            ["path"] = new Dictionary<string, object?>
            {
                ["type"] = "string",
                ["description"] = description
            }
        }
    };

    public static object CreateFileSchema() => new Dictionary<string, object?>
    {
        ["type"] = "object",
        ["properties"] = new Dictionary<string, object?>
        {
            ["path"] = new Dictionary<string, object?>
            {
                ["type"] = "string",
                ["description"] = "Repo-relative path of the file to create."
            },
            ["content"] = new Dictionary<string, object?>
            {
                ["type"] = "string",
                ["description"] = "Full file contents to write."
            }
        },
        ["required"] = new[] { "path", "content" }
    };

    public static object RenameFileSchema() => new Dictionary<string, object?>
    {
        ["type"] = "object",
        ["properties"] = new Dictionary<string, object?>
        {
            ["oldPath"] = new Dictionary<string, object?>
            {
                ["type"] = "string",
                ["description"] = "Existing repo-relative file path."
            },
            ["newPath"] = new Dictionary<string, object?>
            {
                ["type"] = "string",
                ["description"] = "New repo-relative file path."
            }
        },
        ["required"] = new[] { "oldPath", "newPath" }
    };

    public static string ConvertPathArguments(JsonElement arguments)
    {
        return TryGetNonEmptyString(arguments, "path", out var path)
            ? Serialize(new Dictionary<string, string> { ["path"] = path })
            : "";
    }

    public static string ConvertOptionalPathArguments(JsonElement arguments)
    {
        var path = TryGetNonEmptyString(arguments, "path", out var providedPath)
            ? providedPath
            : ".";
        return Serialize(new Dictionary<string, string> { ["path"] = path });
    }

    public static string ConvertCreateFileArguments(JsonElement arguments)
    {
        return TryGetNonEmptyString(arguments, "path", out var path)
            && TryGetString(arguments, "content", out var content)
            ? Serialize(new Dictionary<string, string> { ["path"] = path, ["content"] = content })
            : "";
    }

    public static string ConvertRenameFileArguments(JsonElement arguments)
    {
        return TryGetNonEmptyString(arguments, "oldPath", out var oldPath)
            && TryGetNonEmptyString(arguments, "newPath", out var newPath)
            ? Serialize(new Dictionary<string, string> { ["oldPath"] = oldPath, ["newPath"] = newPath })
            : "";
    }

    public static bool TryParsePathInput(string input, out string path) => TryParseJsonStringProperty(input, "path", out path, allowEmpty: false);

    public static bool TryParseOptionalPathInput(string input, out string path)
    {
        if (TryParseJsonStringProperty(input, "path", out path, allowEmpty: false))
        {
            return true;
        }

        path = ".";
        return string.IsNullOrWhiteSpace(input);
    }

    public static bool TryParseCreateFileInput(string input, out string path, out string content)
    {
        path = "";
        content = "";
        return TryParseJsonStringProperty(input, "path", out path, allowEmpty: false)
            && TryParseJsonStringProperty(input, "content", out content, allowEmpty: true);
    }

    public static bool TryParseRenameFileInput(string input, out string oldPath, out string newPath)
    {
        oldPath = "";
        newPath = "";
        return TryParseJsonStringProperty(input, "oldPath", out oldPath, allowEmpty: false)
            && TryParseJsonStringProperty(input, "newPath", out newPath, allowEmpty: false);
    }

    private static bool TryParseJsonStringProperty(string input, string propertyName, out string value, bool allowEmpty)
    {
        value = "";
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(input);
            return TryGetString(doc.RootElement, propertyName, out value)
                && (allowEmpty || !string.IsNullOrWhiteSpace(value));
        }
        catch
        {
            return false;
        }
    }

    private static bool TryGetNonEmptyString(JsonElement arguments, string propertyName, out string value)
    {
        return TryGetString(arguments, propertyName, out value)
            && !string.IsNullOrWhiteSpace(value);
    }

    private static bool TryGetString(JsonElement arguments, string propertyName, out string value)
    {
        value = "";
        if (arguments.ValueKind != JsonValueKind.Object
            || !arguments.TryGetProperty(propertyName, out JsonElement property)
            || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString() ?? "";
        return true;
    }

    private static string Serialize(Dictionary<string, string> values) => JsonSerializer.Serialize(values);
}
