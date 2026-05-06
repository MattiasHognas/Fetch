namespace Fetch.Core;

public sealed class PathSandbox(string root)
{
    public string Root
    {
        get;
    } = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

    public string Resolve(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            path = ".";
        }

        var full = Path.GetFullPath(Path.Combine(Root, path));
        return !full.StartsWith(Root, StringComparison.OrdinalIgnoreCase)
            ? throw new InvalidOperationException($"Path escapes repo root: {path}")
            : full;
    }

    public string Relative(string fullPath) => Path.GetRelativePath(Root, fullPath);
}
