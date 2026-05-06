namespace Fetch.Tools;

public sealed class ApplyPatchTool(PathSandbox sandbox, SecretPolicy secrets) : ITool
{
    private readonly PathSandbox _sandbox = sandbox; private readonly SecretPolicy _secrets = secrets;

    public string Name => "apply_patch"; public string Description => "Apply simple text replacement. Input: path|||old_text|||new_text"; public ApprovalMode Approval => ApprovalMode.Ask;
    public async Task<string> RunAsync(string input)
    {
        var parts = input.Split("|||", 3);
        if (parts.Length != 3)
        {
            return "Invalid input. Use path|||old_text|||new_text";
        }

        var path = _sandbox.Resolve(parts[0].Trim());
        _secrets.ThrowIfSensitive(path);
        if (!File.Exists(path))
        {
            return $"File not found: {parts[0]}";
        }

        var c = await File.ReadAllTextAsync(path);
        if (!c.Contains(parts[1], StringComparison.Ordinal))
        {
            return "Old text not found. Patch failed.";
        }

        await File.WriteAllTextAsync(path, c.Replace(parts[1], parts[2], StringComparison.Ordinal));
        return "Patch applied successfully.";
    }
}
