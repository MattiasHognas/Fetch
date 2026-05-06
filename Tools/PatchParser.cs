namespace Fetch.Tools;

public enum PatchOperationType
{
    AddFile, UpdateFile, DeleteFile
}
public sealed record PatchOperation(PatchOperationType Type, string Path, string? Content = null, List<PatchHunk>? Hunks = null);
public sealed record PatchHunk(string OldText, string NewText);

public static class PatchParser
{
    public static List<PatchOperation> Parse(string input)
    {
        List<string> lines = [.. input.Replace("\r\n", "\n").Split('\n')];
        if (lines.FirstOrDefault()?.Trim() != "*** Begin Patch")
        {
            throw new InvalidOperationException("Patch must start with *** Begin Patch");
        }

        if (lines.LastOrDefault(l => !string.IsNullOrWhiteSpace(l))?.Trim() != "*** End Patch")
        {
            throw new InvalidOperationException("Patch must end with *** End Patch");
        }

        var ops = new List<PatchOperation>();
        var i = 1;
        while (i < lines.Count)
        {
            var line = lines[i].TrimEnd();
            if (line == "*** End Patch")
            {
                break;
            }

            if (line.StartsWith("*** Add File: ", StringComparison.Ordinal))
            {
                var path = line["*** Add File: ".Length..].Trim();
                i++;
                var content = new List<string>();
                while (i < lines.Count && !lines[i].StartsWith("*** ", StringComparison.Ordinal))
                {
                    content.Add(RemovePatchPrefix(lines[i]));
                    i++;
                }
                ops.Add(new PatchOperation(PatchOperationType.AddFile, path, string.Join('\n', content)));
                continue;
            }
            if (line.StartsWith("*** Delete File: ", StringComparison.Ordinal))
            {
                var path = line["*** Delete File: ".Length..].Trim();
                ops.Add(new PatchOperation(PatchOperationType.DeleteFile, path));
                i++;
                continue;
            }
            if (line.StartsWith("*** Update File: ", StringComparison.Ordinal))
            {
                var path = line["*** Update File: ".Length..].Trim();
                i++;
                var hunks = new List<PatchHunk>();
                while (i < lines.Count && !lines[i].StartsWith("*** ", StringComparison.Ordinal))
                {
                    if (string.IsNullOrWhiteSpace(lines[i]) || lines[i].StartsWith("@@", StringComparison.Ordinal))
                    {
                        i++;
                        continue;
                    }
                    var oldLines = new List<string>();
                    var newLines = new List<string>();
                    while (i < lines.Count && lines[i].StartsWith('-'))
                    {
                        oldLines.Add(lines[i][1..]);
                        i++;
                    } while (i < lines.Count && lines[i].StartsWith('+'))
                    {
                        newLines.Add(lines[i][1..]);
                        i++;
                    }
                    if (oldLines.Count == 0 && newLines.Count == 0)
                    {
                        i++;
                        continue;
                    }
                    hunks.Add(new PatchHunk(string.Join('\n', oldLines), string.Join('\n', newLines)));
                }
                ops.Add(new PatchOperation(PatchOperationType.UpdateFile, path, Hunks: hunks));
                continue;
            }
            i++;
        }
        return ops;
    }
    private static string RemovePatchPrefix(string line) => line.StartsWith('+') ? line[1..] : line;
}
