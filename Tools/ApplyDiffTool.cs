using System.Text.Json;

namespace Fetch.Tools;

public sealed class ApplyDiffTool(FileReadRegistry registry, PathSandbox sandbox, SecretPolicy secrets, AgentConfig config) : ITool, IPreviewableTool
{
    private readonly FileReadRegistry _registry = registry; private readonly PathSandbox _sandbox = sandbox; private readonly SecretPolicy _secrets = secrets; private readonly AgentConfig _config = config;

    public string Name => "apply_diff";
    public string Description => "Apply an agent patch using *** Begin Patch format. Supports Add File, Update File, Delete File. Input may be the raw patch string or JSON like {\"patch\":\"*** Begin Patch...\"}.";
    public ApprovalMode Approval => ApprovalMode.Ask;
    public async Task<string> PreviewAsync(string input)
    {
        try
        {
            List<PatchOperation> ops = PatchParser.Parse(ExtractPatchInput(input));
            (var Success, var Message) = await DryRunAsync(ops);
            return Success ? Message : $"Patch validation failed:\n{Message}";
        }
        catch (Exception ex) { return $"Patch parse failed: {ex.Message}"; }
    }
    public async Task<string> RunAsync(string input)
    {
        List<PatchOperation> ops;
        try
        {
            ops = PatchParser.Parse(ExtractPatchInput(input));
        }
        catch (Exception ex) { return Failure("patch_parse_failed", ex.Message); }
        (var Success, var Message) = await DryRunAsync(ops);
        if (!Success)
        {
            return Failure("patch_validation_failed", Message);
        }

        _ = Directory.CreateDirectory(_config.BackupRoot);
        try
        {
            foreach (PatchOperation op in ops)
            {
                await ApplyAsync(op);
            }
            return $"Patch applied successfully.\n\nOperations:\n{Message}";
        }
        catch (Exception ex) { return Failure("patch_apply_failed", ex.Message); }
    }

    private static string ExtractPatchInput(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return input;
        }

        var trimmed = input.Trim();
        if (trimmed.StartsWith("*** Begin Patch", StringComparison.Ordinal))
        {
            return input;
        }

        try
        {
            using var doc = JsonDocument.Parse(input);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("patch", out JsonElement patch)
                && patch.ValueKind == JsonValueKind.String)
            {
                return patch.GetString() ?? "";
            }
        }
        catch
        {
        }

        return input;
    }

    private async Task<(bool Success, string Message)> DryRunAsync(List<PatchOperation> ops)
    {
        var msgs = new List<string>();
        foreach (PatchOperation op in ops)
        {
            var path = _sandbox.Resolve(op.Path);
            _secrets.ThrowIfSensitive(path);
            switch (op.Type)
            {
                case PatchOperationType.AddFile:
                    if (File.Exists(path))
                    {
                        return (false, $"File already exists: {op.Path}");
                    }

                    msgs.Add($"ADD {op.Path}");
                    break;
                case PatchOperationType.UpdateFile:
                    if (!File.Exists(path))
                    {
                        return (false, $"File not found: {op.Path}");
                    }

                    var content = await File.ReadAllTextAsync(path);
                    if (!_registry.WasReadAndUnchanged(path, content))
                    {
                        return (false, $"Stale edit: read file again before editing: {op.Path}");
                    }

                    var test = content;
                    var idx = 0;
                    foreach (PatchHunk h in op.Hunks ?? [])
                    {
                        idx++;
                        if (string.IsNullOrEmpty(h.OldText))
                        {
                            return (false, $"Empty old_text in hunk {idx} for {op.Path}");
                        }

                        var count = Count(test, h.OldText);
                        if (count == 0)
                        {
                            return (false, $"Old text not found in {op.Path}, hunk {idx}. Read the file/range again and use exact current text.\nRequired old text:\n{h.OldText}");
                        }

                        if (count > 1)
                        {
                            return (false, $"Old text is ambiguous in {op.Path}, hunk {idx}; appears {count} times. Use more surrounding context.");
                        }

                        test = test.Replace(h.OldText, h.NewText, StringComparison.Ordinal);
                    }
                    msgs.Add($"UPDATE {op.Path} ({op.Hunks?.Count ?? 0} hunk(s))");
                    break;
                case PatchOperationType.DeleteFile:
                    if (!File.Exists(path))
                    {
                        return (false, $"File not found: {op.Path}");
                    }

                    var c = await File.ReadAllTextAsync(path);
                    if (!_registry.WasReadAndUnchanged(path, c))
                    {
                        return (false, $"Stale delete: read file again before deleting: {op.Path}");
                    }

                    msgs.Add($"DELETE {op.Path}");
                    break;
                default:
                    break;
            }
        }
        return (true, string.Join('\n', msgs));
    }
    private async Task ApplyAsync(PatchOperation op)
    {
        var path = _sandbox.Resolve(op.Path);
        _secrets.ThrowIfSensitive(path);
        switch (op.Type)
        {
            case PatchOperationType.AddFile:
                await AtomicWriteAsync(path, op.Content ?? "");
                break;
            case PatchOperationType.UpdateFile:
                Backup(path);
                var content = await File.ReadAllTextAsync(path);
                foreach (PatchHunk h in op.Hunks ?? [])
                {
                    content = content.Replace(h.OldText, h.NewText, StringComparison.Ordinal);
                }
                await AtomicWriteAsync(path, content);
                break;
            case PatchOperationType.DeleteFile:
                Backup(path);
                File.Delete(path);
                break;
            default:
                break;
        }
    }
    private static int Count(string haystack, string needle)
    {
        var count = 0;
        var i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0)
        {
            count++;
            i += needle.Length;
        }
        return count;
    }
    private static async Task AtomicWriteAsync(string path, string content)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            _ = Directory.CreateDirectory(dir);
        }

        var temp = path + $".tmp-{Guid.NewGuid():N}";
        await File.WriteAllTextAsync(temp, content);
        if (File.Exists(path))
        {
            File.Replace(temp, path, null);
        }
        else
        {
            File.Move(temp, path);
        }
    }
    private void Backup(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }
        _ = Directory.CreateDirectory(_config.BackupRoot);
        var safe = path.Replace(Path.DirectorySeparatorChar, '_')
            .Replace(Path.AltDirectorySeparatorChar, '_')
            .Replace(':', '_');
        File.Copy(path, Path.Combine(_config.BackupRoot, $"{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}_{safe}"), false);
    }
    private static string Failure(string kind, string message) => $"Patch failed.\n\nKind: {kind}\n\nReason:\n{message}\n\nThe agent should retry with a smaller, exact patch after rereading the relevant file/range.";
}
