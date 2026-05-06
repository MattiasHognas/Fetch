using Microsoft.Extensions.FileSystemGlobbing;

namespace Fetch.Tools;

public sealed class IgnoreRules
{
    private readonly Matcher _matcher = new();
    public IgnoreRules(AgentConfig config, string root = ".")
    {
        _ = _matcher.AddInclude("**/*");
        foreach (var p in config.AlwaysIgnoredPaths)
        {
            AddIgnore(p.TrimEnd('/') + "/");
        }

        var gitignore = Path.Combine(root, ".gitignore");
        if (File.Exists(gitignore))
        {
            foreach (var line in File.ReadAllLines(gitignore))
            {
                var rule = line.Trim();
                if (string.IsNullOrWhiteSpace(rule) || rule.StartsWith('#') || rule.StartsWith('!'))
                {
                    continue;
                }

                AddIgnore(rule);
            }
        }
    }
    public bool IsIgnored(string path)
    {
        var normalized = path.Replace('\\', '/').Trim();
        normalized = normalized.StartsWith("./", StringComparison.Ordinal)
            ? normalized[2..]
            : normalized.TrimStart('/');
        return !_matcher.Match(normalized).HasMatches;
    }
    private void AddIgnore(string pattern)
    {
        pattern = pattern.Replace('\\', '/').Trim().TrimStart('/');
        if (pattern.EndsWith('/'))
        {
            var directory = pattern.TrimEnd('/');
            _ = _matcher.AddExclude(directory);
            _ = _matcher.AddExclude(directory + "/**");
        }
        else
        {
            _ = _matcher.AddExclude(pattern);
            _ = _matcher.AddExclude("**/" + pattern);
        }
    }
}
