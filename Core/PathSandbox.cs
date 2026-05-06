namespace Fetch.Core;

public sealed class PathSandbox(string root)
{
    private readonly string _normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

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
        var normalizedFull = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return normalizedFull != _normalizedRoot && !normalizedFull.StartsWith(Root, StringComparison.OrdinalIgnoreCase)
            ? throw new InvalidOperationException($"Path escapes repo root: {path}")
            : full;
    }

    public string Relative(string fullPath) => Path.GetRelativePath(Root, fullPath);
}
