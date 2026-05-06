using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fetch.Tools;

namespace Fetch.Indexing;

public sealed record CodeChunk(string File, int StartLine, int EndLine, string Text, float[] Embedding);
public sealed record SemanticIndexStats(int IndexedFiles, int ReusedFiles, int RemovedFiles, int TotalChunks);

public sealed class FileHashStore(string root)
{
    private readonly string _path = Path.Combine(root, "filehashes.json");

    public async Task<Dictionary<string, string>> LoadAsync() => File.Exists(_path) ? JsonSerializer.Deserialize<Dictionary<string, string>>(await File.ReadAllTextAsync(_path), AgentConfig.JsonOptions()) ?? [] : [];
    public async Task SaveAsync(Dictionary<string, string> h)
    {
        _ = Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        await File.WriteAllTextAsync(_path, JsonSerializer.Serialize(h, AgentConfig.JsonOptions()));
    }
    public static string Compute(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}

public sealed class SemanticIndex(AgentConfig config, PathSandbox sandbox, SecretPolicy secrets, EmbeddingClient embeddings, IgnoreRules ignore)
{
    private readonly AgentConfig _config = config; private readonly PathSandbox _sandbox = sandbox; private readonly SecretPolicy _secrets = secrets; private readonly EmbeddingClient _embeddings = embeddings; private readonly IgnoreRules _ignore = ignore;
    private string IndexPath => Path.Combine(_config.IndexRoot, "chunks.json"); public bool Exists => File.Exists(IndexPath);

    public async Task<SemanticIndexStats> BuildAsync()
    {
        _ = Directory.CreateDirectory(_config.IndexRoot);
        var hashStore = new FileHashStore(_config.IndexRoot);
        Dictionary<string, string> prev = await hashStore.LoadAsync();
        var newHashes = new Dictionary<string, string>();
        var all = new List<CodeChunk>();
        if (File.Exists(IndexPath))
        {
            all = JsonSerializer.Deserialize<List<CodeChunk>>(await File.ReadAllTextAsync(IndexPath), AgentConfig.JsonOptions()) ?? [];
        }

        var files = Directory.GetFiles(_sandbox.Root, "*.*", SearchOption.AllDirectories).Where(IsIndexable).ToList();
        var updated = new List<CodeChunk>();
        int indexed = 0, reused = 0;
        foreach (var file in files)
        {
            _secrets.ThrowIfSensitive(file);
            var text = await File.ReadAllTextAsync(file);
            var rel = _sandbox.Relative(file);
            var hash = FileHashStore.Compute(text);
            newHashes[rel] = hash;
            if (prev.TryGetValue(rel, out var old) && old == hash)
            {
                updated.AddRange(all.Where(c => c.File == rel));
                reused++;
                continue;
            }
            foreach (CodeChunk chunk in ChunkFile(rel, text))
            {
                var emb = await _embeddings.EmbedAsync(chunk.Text);
                updated.Add(chunk with
                {
                    Embedding = emb
                });
            }
            indexed++;
        }
        var removed = prev.Keys.Count(k => !newHashes.ContainsKey(k));
        updated = [.. updated.Where(c => newHashes.ContainsKey(c.File))];
        await File.WriteAllTextAsync(IndexPath, JsonSerializer.Serialize(updated, AgentConfig.JsonOptions()));
        await hashStore.SaveAsync(newHashes);
        return new SemanticIndexStats(indexed, reused, removed, updated.Count);
    }
    public async Task<string> SearchAsync(string query, int? topK = null)
    {
        if (!File.Exists(IndexPath))
        {
            return "Semantic index missing. Run /index first.";
        }

        List<CodeChunk> chunks = JsonSerializer.Deserialize<List<CodeChunk>>(await File.ReadAllTextAsync(IndexPath), AgentConfig.JsonOptions()) ?? [];
        var q = await _embeddings.EmbedAsync(query);
        var res = chunks
            .Select(c => new
            {
                Chunk = c,
                Score = Cosine(q, c.Embedding) + ScoreBoost(query, c)
            })
            .OrderByDescending(x => x.Score)
            .Take(topK ?? _config.SemanticSearchTopK);
        return string.Join("\n\n", res.Select(r => $"Score: {r.Score:F3}\nFile: {r.Chunk.File}:{r.Chunk.StartLine}-{r.Chunk.EndLine}\n{r.Chunk.Text}"));
    }
    private IEnumerable<CodeChunk> ChunkFile(string rel, string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var cur = new List<string>();
        var start = 1;
        var chars = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            cur.Add(lines[i]);
            chars += lines[i].Length + 1;
            if (chars < _config.ChunkMaxChars)
            {
                continue;
            }

            yield return new CodeChunk(rel, start, i + 1, string.Join('\n', cur), []);
            var overlap = TakeOverlap(cur).ToList();
            cur = overlap;
            start = Math.Max(1, i + 1 - cur.Count + 1);
            chars = cur.Sum(x => x.Length + 1);
        }
        if (cur.Count > 0)
        {
            yield return new CodeChunk(rel, start, lines.Length, string.Join('\n', cur), []);
        }
    }
    private List<string> TakeOverlap(List<string> lines)
    {
        var r = new List<string>();
        var chars = 0;
        for (var i = lines.Count - 1; i >= 0; i--)
        {
            r.Insert(0, lines[i]);
            chars += lines[i].Length + 1;
            if (chars >= _config.ChunkOverlapChars)
            {
                break;
            }
        }
        return r;
    }
    private bool IsIndexable(string path)
    {
        if (path.Contains(Path.DirectorySeparatorChar + ".git" + Path.DirectorySeparatorChar) || path.Contains(Path.DirectorySeparatorChar + ".agent" + Path.DirectorySeparatorChar))
        {
            return false;
        }

        if (_secrets.IsSensitivePath(path))
        {
            return false;
        }

        var rel = _sandbox.Relative(path);
        if (_ignore.IsIgnored(rel) || IsLowSignalIndexedPath(rel))
        {
            return false;
        }

        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".cs" or ".ts" or ".tsx" or ".js" or ".jsx" or ".rs" or ".py" or ".go" or ".java" or ".kt" or ".md" or ".json" or ".yml" or ".yaml" or ".toml";
    }

    private static float ScoreBoost(string query, CodeChunk chunk)
    {
        var normalizedQuery = query.ToLowerInvariant();
        var normalizedFile = chunk.File.Replace('\\', '/').ToLowerInvariant();
        var ext = Path.GetExtension(normalizedFile);
        var boost = 0f;

        if (ext == ".cs")
        {
            boost += 0.08f;
        }
        else if (ext is ".ts" or ".tsx" or ".js" or ".jsx" or ".rs" or ".py" or ".go" or ".java" or ".kt")
        {
            boost += 0.05f;
        }
        else if (ext is ".json" or ".yml" or ".yaml" or ".toml")
        {
            boost -= 0.08f;
        }

        if (normalizedQuery.Contains("class", StringComparison.Ordinal)
            || normalizedQuery.Contains("architecture", StringComparison.Ordinal)
            || normalizedQuery.Contains("relationship", StringComparison.Ordinal)
            || normalizedQuery.Contains("flow", StringComparison.Ordinal))
        {
            if (chunk.Text.Contains("class ", StringComparison.Ordinal)
                || chunk.Text.Contains("record ", StringComparison.Ordinal)
                || chunk.Text.Contains("interface ", StringComparison.Ordinal)
                || chunk.Text.Contains("namespace ", StringComparison.Ordinal))
            {
                boost += 0.12f;
            }

            if (normalizedFile.EndsWith("program.cs", StringComparison.Ordinal)
                || normalizedFile.Contains("core/", StringComparison.Ordinal)
                || normalizedFile.Contains("runtime/", StringComparison.Ordinal)
                || normalizedFile.Contains("planning/", StringComparison.Ordinal)
                || normalizedFile.Contains("prompts/", StringComparison.Ordinal)
                || normalizedFile.Contains("tools/", StringComparison.Ordinal)
                || normalizedFile.Contains("slash/", StringComparison.Ordinal)
                || normalizedFile.Contains("lsp/", StringComparison.Ordinal))
            {
                boost += 0.06f;
            }
        }

        if (IsLowSignalIndexedPath(normalizedFile))
        {
            boost -= 0.2f;
        }

        return boost;
    }

    private static bool IsLowSignalIndexedPath(string path)
    {
        var normalized = path.Replace('\\', '/').ToLowerInvariant();
        return normalized.StartsWith(".vscode/", StringComparison.Ordinal)
            || normalized.StartsWith("properties/", StringComparison.Ordinal)
            || normalized.EndsWith("launchsettings.json", StringComparison.Ordinal)
            || normalized.EndsWith("launch.json", StringComparison.Ordinal)
            || normalized.EndsWith("settings.json", StringComparison.Ordinal)
            || normalized.EndsWith("extensions.json", StringComparison.Ordinal);
    }
    private static float Cosine(float[] a, float[] b)
    {
        var dot = 0f;
        var ma = 0f;
        var mb = 0f;
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            dot += a[i] * b[i];
            ma += a[i] * a[i];
            mb += b[i] * b[i];
        }
        return dot / (((float)Math.Sqrt(ma) * (float)Math.Sqrt(mb)) + 1e-8f);
    }
}

public sealed class SemanticSearchTool(SemanticIndex index) : ITool
{
    private readonly SemanticIndex _index = index;

    public string Name => "semantic_search"; public string Description => "Search repo semantically by meaning, not exact text. Input: natural language query."; public ApprovalMode Approval => ApprovalMode.Auto;
    public Task<string> RunAsync(string input) => _index.SearchAsync(input);
}
