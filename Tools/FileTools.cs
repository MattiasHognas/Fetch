using System.Text.Json;

namespace Fetch.Tools;

public sealed class ReadFileTool(FileReadRegistry registry, PathSandbox sandbox, SecretPolicy secrets) : ITool
{
    private readonly FileReadRegistry _registry = registry; private readonly PathSandbox _sandbox = sandbox; private readonly SecretPolicy _secrets = secrets;

    public string Name => "read_file"; public string Description => "Read a file by path."; public ApprovalMode Approval => ApprovalMode.Auto;
    public async Task<string> RunAsync(string input)
    {
        var rawPath = ExtractPathInput(input);
        var path = _sandbox.Resolve(rawPath);
        _secrets.ThrowIfSensitive(path);
        if (!File.Exists(path))
        {
            return $"File not found: {input}";
        }

        var c = await File.ReadAllTextAsync(path);
        _registry.MarkRead(path, c);
        return c;
    }

    private static string ExtractPathInput(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return input.Trim();
        }

        try
        {
            using var doc = JsonDocument.Parse(input);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("path", out JsonElement path)
                && path.ValueKind == JsonValueKind.String)
            {
                return path.GetString()?.Trim() ?? "";
            }
        }
        catch
        {
        }

        return input.Trim();
    }
}

public sealed class GetFileSummaryTool(FileReadRegistry registry, PathSandbox sandbox, SecretPolicy secrets) : ITool
{
    private readonly FileReadRegistry _registry = registry; private readonly PathSandbox _sandbox = sandbox; private readonly SecretPolicy _secrets = secrets;

    public string Name => "read_head"; public string Description => "Read the first 100 lines of a file."; public ApprovalMode Approval => ApprovalMode.Auto;
    public async Task<string> RunAsync(string input)
    {
        var path = _sandbox.Resolve(input.Trim());
        _secrets.ThrowIfSensitive(path);
        if (!File.Exists(path))
        {
            return $"Not found: {input}";
        }

        var c = await File.ReadAllTextAsync(path);
        _registry.MarkRead(path, c);
        return string.Join("\n", c.Replace("\r\n", "\n").Split('\n').Take(100));
    }
}

public sealed class ListFilesTool(PathSandbox sandbox) : ITool
{
    private readonly PathSandbox _sandbox = sandbox;

    public string Name => "list_files"; public string Description => "List files in a directory."; public ApprovalMode Approval => ApprovalMode.Auto;
    public Task<string> RunAsync(string input)
    {
        var p = _sandbox.Resolve(string.IsNullOrWhiteSpace(input) ? "." : input.Trim());
        return !Directory.Exists(p)
        ? Task.FromResult($"Directory not found: {input}")
        : Task.FromResult(string.Join("\n", Directory.GetFiles(p).Select(_sandbox.Relative)));
    }
}

public sealed class CreateFileTool(PathSandbox sandbox, SecretPolicy secrets) : ITool
{
    private readonly PathSandbox _sandbox = sandbox; private readonly SecretPolicy _secrets = secrets;

    public string Name => "create_file"; public string Description => "Create a file. Input: JSON {\"path\":\"...\",\"content\":\"...\"} or legacy 'path|||content'."; public ApprovalMode Approval => ApprovalMode.Ask;
    public async Task<string> RunAsync(string input)
    {
        if (!TryParseInput(input, out var rawPath, out var content))
        {
            return "Invalid input. Provide JSON {\"path\":\"docs/file.md\",\"content\":\"...\"} or use the legacy 'path|||content' format. Bare paths without content are not accepted.";
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

    private static bool TryParseInput(string input, out string path, out string content)
    {
        path = "";
        content = "";
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        var trimmed = input.Trim();
        if (trimmed.StartsWith('{'))
        {
            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("path", out JsonElement pathEl)
                    && pathEl.ValueKind == JsonValueKind.String)
                {
                    path = pathEl.GetString() ?? "";
                    if (doc.RootElement.TryGetProperty("content", out JsonElement contentEl)
                        && contentEl.ValueKind == JsonValueKind.String)
                    {
                        content = contentEl.GetString() ?? "";
                    }
                    return !string.IsNullOrWhiteSpace(path);
                }
            }
            catch
            {
            }
        }

        var parts = input.Split("|||", 2);
        if (parts.Length == 2 && !string.IsNullOrWhiteSpace(parts[0]))
        {
            path = parts[0];
            content = parts[1];
            return true;
        }

        return false;
    }
}

public sealed class DeleteFileTool(PathSandbox sandbox, SecretPolicy secrets) : ITool
{
    private readonly PathSandbox _sandbox = sandbox; private readonly SecretPolicy _secrets = secrets;

    public string Name => "delete_file"; public string Description => "Delete a file by path."; public ApprovalMode Approval => ApprovalMode.Ask;
    public Task<string> RunAsync(string input)
    {
        var path = _sandbox.Resolve(input.Trim());
        _secrets.ThrowIfSensitive(path);
        if (!File.Exists(path))
        {
            return Task.FromResult($"File not found: {input}");
        }

        File.Delete(path);
        return Task.FromResult($"Deleted file: {input}");
    }
}

public sealed class RenameFileTool(PathSandbox sandbox, SecretPolicy secrets) : ITool
{
    private readonly PathSandbox _sandbox = sandbox; private readonly SecretPolicy _secrets = secrets;

    public string Name => "rename_file"; public string Description => "Rename file. Input: old|||new"; public ApprovalMode Approval => ApprovalMode.Ask;
    public Task<string> RunAsync(string input)
    {
        var parts = input.Split("|||", 2);
        if (parts.Length != 2)
        {
            return Task.FromResult("Invalid input. Use old|||new");
        }

        var oldp = _sandbox.Resolve(parts[0].Trim());
        var newp = _sandbox.Resolve(parts[1].Trim());
        _secrets.ThrowIfSensitive(oldp);
        _secrets.ThrowIfSensitive(newp);
        if (!File.Exists(oldp))
        {
            return Task.FromResult($"Not found: {parts[0]}");
        }

        _ = Directory.CreateDirectory(Path.GetDirectoryName(newp)!);
        File.Move(oldp, newp);
        return Task.FromResult($"Renamed to: {parts[1]}");
    }
}
