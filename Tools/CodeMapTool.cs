using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Fetch.Tools;

/// <summary>
/// Returns a compact, line-budgeted map of the repo: directory -> file -> top-level types -> public members.
/// Uses LSP <c>textDocument/documentSymbol</c> for every supported language; files whose language has no
/// configured LSP server are listed without symbol detail.
/// Designed to be the FIRST tool called for architecture / overview / refactor-planning tasks so the agent
/// does not waste its step budget walking <c>repo_tree</c> and reading random files.
/// </summary>
public sealed class CodeMapTool(
    AgentConfig config,
    PathSandbox sandbox,
    IgnoreRules ignore,
    LspServerSelector lspSelector) : ITool
{
    private readonly AgentConfig _config = config;
    private readonly PathSandbox _sandbox = sandbox;
    private readonly IgnoreRules _ignore = ignore;
    private readonly LspServerSelector _lspSelector = lspSelector;

    private const int MaxOutputChars = 6000;
    private const int MaxFiles = 200;
    private const int MaxMembersPerType = 12;

    public string Name => "code_map";
    public string Description =>
        "Get a repository code map: every source file with its top-level classes/types and public members. "
        + "Use this FIRST for architecture, overview, refactor-planning, or 'where is X' tasks before reading individual files. "
        + "Optional input JSON {\"path\":\"sub/dir\",\"include\":\"*.cs\"} to scope. Empty input maps the whole repo.";
    public ApprovalMode Approval => ApprovalMode.Auto;

    public async Task<string> RunAsync(string input)
    {
        (var scopePath, var includeGlob) = ParseInput(input);
        List<string> files = SelectFiles(scopePath, includeGlob);
        if (files.Count == 0)
        {
            return "code_map: no source files found for the given scope.";
        }

        var byLanguage = files
            .GroupBy(GetLanguageKey)
            .ToDictionary(g => g.Key, g => g.Select(f => new FileSymbols(f)).ToList());

        var lspExtractor = new LspDocumentSymbolExtractor(_config, _sandbox, _lspSelector);
        try
        {
            foreach ((var language, List<FileSymbols> group) in byLanguage)
            {
                LspServerConfig? server = lspExtractor.SelectServerForLanguage(language);
                if (server is null)
                {
                    foreach (FileSymbols fs in group)
                    {
                        fs.Note = $"no LSP server configured for {language}";
                    }
                    continue;
                }

                using var lspCts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(15, _config.Lsp.RequestTimeoutSeconds * 2)));
                try
                {
                    await lspExtractor.PopulateAsync(server, group, lspCts.Token);
                }
                catch (Exception ex)
                {
                    foreach (FileSymbols fs in group)
                    {
                        if (fs.Types.Count == 0 && string.IsNullOrEmpty(fs.Note))
                        {
                            fs.Note = $"LSP {server.Id} failed: {ex.Message}";
                        }
                    }
                }
            }
        }
        finally
        {
            await lspExtractor.DisposeAsync();
        }

        return Render(byLanguage.Values.SelectMany(g => g).OrderBy(f => f.RelativePath, StringComparer.Ordinal));
    }

    private List<string> SelectFiles(string? scopePath, string? includeGlob)
    {
        var rootPath = ResolveScopeRoot(scopePath);

        Func<string, bool> matchesGlob = string.IsNullOrWhiteSpace(includeGlob)
            ? IsIndexable
            : path => GlobMatch(includeGlob, Path.GetFileName(path));

        var files = new List<string>();
        foreach (var file in Directory.EnumerateFiles(rootPath, "*.*", SearchOption.AllDirectories))
        {
            var rel = _sandbox.Relative(file);
            if (_ignore.IsIgnored(rel))
            {
                continue;
            }
            if (!matchesGlob(file))
            {
                continue;
            }
            files.Add(rel);
            if (files.Count >= MaxFiles)
            {
                break;
            }
        }
        files.Sort(StringComparer.Ordinal);
        return files;
    }

    private string ResolveScopeRoot(string? scopePath)
    {
        if (string.IsNullOrWhiteSpace(scopePath))
        {
            return _sandbox.Root;
        }

        try
        {
            var resolved = _sandbox.Resolve(scopePath);
            return Directory.Exists(resolved) ? resolved : _sandbox.Root;
        }
        catch
        {
            return _sandbox.Root;
        }
    }

    private static bool IsIndexable(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".cs" or ".ts" or ".tsx" or ".js" or ".jsx" or ".py" or ".go" or ".rs"
            or ".java" or ".kt" or ".rb" or ".php" or ".swift";
    }

    private static bool GlobMatch(string glob, string name)
    {
        var pattern = "^" + Regex.Escape(glob).Replace("\\*", ".*").Replace("\\?", ".") + "$";
        return Regex.IsMatch(name, pattern, RegexOptions.IgnoreCase);
    }

    private static string GetLanguageKey(string relativePath)
    {
        var ext = Path.GetExtension(relativePath).ToLowerInvariant();
        return ext switch
        {
            ".cs" => "csharp",
            ".ts" or ".tsx" or ".js" or ".jsx" => "typescript",
            ".py" => "python",
            ".go" => "go",
            ".rs" => "rust",
            ".java" => "java",
            ".kt" => "kotlin",
            ".rb" => "ruby",
            ".php" => "php",
            ".swift" => "swift",
            _ => "other"
        };
    }

    private static (string? path, string? include) ParseInput(string input)
    {
        var trimmed = input?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return (null, null);
        }

        if (trimmed.StartsWith('{'))
        {
            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                JsonElement root = doc.RootElement;
                var path = root.TryGetProperty("path", out JsonElement p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
                var include = root.TryGetProperty("include", out JsonElement i) && i.ValueKind == JsonValueKind.String ? i.GetString() : null;
                return (path, include);
            }
            catch
            {
                return (trimmed, null);
            }
        }

        return (trimmed, null);
    }

    private static string Render(IEnumerable<FileSymbols> files)
    {
        var sb = new StringBuilder();
        var lastDir = "";
        var truncated = false;
        var fileCount = 0;
        foreach (FileSymbols fs in files)
        {
            var dir = Path.GetDirectoryName(fs.RelativePath)?.Replace('\\', '/') ?? "";
            if (dir != lastDir)
            {
                if (sb.Length > 0)
                {
                    _ = sb.Append('\n');
                }
                _ = sb.Append(string.IsNullOrEmpty(dir) ? "(root)" : dir).Append("/\n");
                lastDir = dir;
            }
            _ = sb.Append("  ").Append(Path.GetFileName(fs.RelativePath)).Append('\n');
            fileCount++;

            if (fs.Types.Count == 0 && string.IsNullOrEmpty(fs.Note))
            {
                continue;
            }
            if (!string.IsNullOrEmpty(fs.Note))
            {
                _ = sb.Append("    [").Append(fs.Note).Append("]\n");
            }
            foreach (TypeSymbol type in fs.Types.Take(20))
            {
                _ = sb.Append("    ").Append(type.Kind).Append(' ').Append(type.Name);
                if (!string.IsNullOrEmpty(type.BaseList))
                {
                    _ = sb.Append(" : ").Append(type.BaseList);
                }
                _ = sb.Append('\n');
                IEnumerable<string> members = type.Members.Take(MaxMembersPerType);
                if (members.Any())
                {
                    _ = sb.Append("      ").Append(string.Join(", ", members)).Append('\n');
                }
                if (type.Members.Count > MaxMembersPerType)
                {
                    _ = sb.Append("      ... +").Append(type.Members.Count - MaxMembersPerType).Append(" more\n");
                }
            }

            if (sb.Length > MaxOutputChars)
            {
                truncated = true;
                break;
            }
        }
        if (truncated)
        {
            _ = sb.Append("\n[code_map truncated; pass {\"path\":\"sub/dir\"} to focus]");
        }
        _ = sb.Insert(0, $"code_map: {fileCount} file(s)\n\n");
        return sb.ToString();
    }
}

internal sealed class FileSymbols(string relativePath)
{
    public string RelativePath { get; } = relativePath;
    public List<TypeSymbol> Types { get; } = [];
    public string Note { get; set; } = "";
}

internal sealed class TypeSymbol(string kind, string name)
{
    public string Kind { get; } = kind;
    public string Name { get; } = name;
    public string BaseList { get; set; } = "";
    public List<string> Members { get; } = [];
}

internal sealed class LspDocumentSymbolExtractor(AgentConfig config, PathSandbox sandbox, LspServerSelector selector) : IAsyncDisposable
{
    private readonly AgentConfig _config = config;
    private readonly PathSandbox _sandbox = sandbox;
    private readonly LspServerSelector _selector = selector;
    private readonly Dictionary<string, LspClient> _clients = [];

    public LspServerConfig? SelectServerForLanguage(string languageKey)
    {
        return !_config.Lsp.Enabled
            ? null
            : _config.Lsp.Servers
            .FirstOrDefault(s => s.Enabled
                && string.Equals(s.Language, languageKey, StringComparison.OrdinalIgnoreCase)
                && _selector.RootMarkerOk(s)
                && LspServerSelector.CommandOk(s));
    }

    public async Task PopulateAsync(LspServerConfig server, List<FileSymbols> files, CancellationToken ct)
    {
        if (!_clients.TryGetValue(server.Id, out LspClient? client))
        {
            client = new LspClient(server, _sandbox);
            _clients[server.Id] = client;
            await InitializeAsync(client, ct);
        }

        foreach (FileSymbols fs in files)
        {
            if (ct.IsCancellationRequested)
            {
                break;
            }
            try
            {
                var absPath = _sandbox.Resolve(fs.RelativePath);
                if (!File.Exists(absPath))
                {
                    continue;
                }
                var text = await File.ReadAllTextAsync(absPath, ct);
                var uri = new Uri(absPath).AbsoluteUri;
                await client.NotifyAsync("textDocument/didOpen", new
                {
                    textDocument = new
                    {
                        uri,
                        languageId = server.Language,
                        version = 1,
                        text
                    }
                }, ct);
                JsonElement result = await client.RequestAsync("textDocument/documentSymbol", new
                {
                    textDocument = new
                    {
                        uri
                    }
                }, ct);
                ParseDocumentSymbols(result, fs);
                await client.NotifyAsync("textDocument/didClose", new
                {
                    textDocument = new
                    {
                        uri
                    }
                }, ct);
            }
            catch
            {
                // Leave fs.Types empty; the file will appear without symbol detail.
            }
        }
    }

    private async Task InitializeAsync(LspClient client, CancellationToken ct)
    {
        var rootUri = new Uri(_sandbox.Root).AbsoluteUri;
        _ = await client.RequestAsync("initialize", new
        {
            processId = Environment.ProcessId,
            rootUri,
            capabilities = new
            {
                textDocument = new
                {
                    documentSymbol = new
                    {
                        dynamicRegistration = false,
                        hierarchicalDocumentSymbolSupport = true
                    },
                    synchronization = new
                    {
                        dynamicRegistration = false
                    }
                }
            }
        }, ct);
        await client.NotifyAsync("initialized", new
        {
        }, ct);
    }

    private static void ParseDocumentSymbols(JsonElement result, FileSymbols fs)
    {
        if (result.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (JsonElement sym in result.EnumerateArray())
        {
            // Hierarchical DocumentSymbol[] has "name" and "children".
            // Flat SymbolInformation[] has "name" and "containerName".
            if (sym.TryGetProperty("children", out _) || sym.TryGetProperty("range", out _))
            {
                AddHierarchical(sym, fs, depth: 0);
            }
            else
            {
                AddFlat(sym, fs);
            }
        }
    }

    private static readonly Dictionary<int, string> KindNames = new()
    {
        [5] = "class",
        [10] = "enum",
        [11] = "interface",
        [22] = "struct",
        [12] = "function",
        [6] = "method",
        [9] = "constructor",
        [8] = "field",
        [7] = "property",
        [14] = "constant",
        [4] = "namespace",
        [2] = "module",
        [13] = "variable",
        [23] = "event"
    };

    private static void AddHierarchical(JsonElement sym, FileSymbols fs, int depth)
    {
        var name = sym.TryGetProperty("name", out JsonElement n) ? n.GetString() ?? "" : "";
        var kindNum = sym.TryGetProperty("kind", out JsonElement k) ? k.GetInt32() : 0;
        var kindName = KindNames.TryGetValue(kindNum, out var kn) ? kn : "symbol";
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        if (IsTypeKind(kindNum))
        {
            var type = new TypeSymbol(kindName, name);
            fs.Types.Add(type);
            if (sym.TryGetProperty("children", out JsonElement children) && children.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement child in children.EnumerateArray())
                {
                    var cName = child.TryGetProperty("name", out JsonElement cn) ? cn.GetString() ?? "" : "";
                    var cKind = child.TryGetProperty("kind", out JsonElement ck) ? ck.GetInt32() : 0;
                    if (string.IsNullOrWhiteSpace(cName) || cName.StartsWith('_'))
                    {
                        continue;
                    }
                    if (IsMemberKind(cKind))
                    {
                        type.Members.Add(cName);
                    }
                    else if (IsTypeKind(cKind))
                    {
                        AddHierarchical(child, fs, depth + 1);
                    }
                }
            }
        }
        else if (sym.TryGetProperty("children", out JsonElement children) && children.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement child in children.EnumerateArray())
            {
                AddHierarchical(child, fs, depth + 1);
            }
        }
    }

    private static void AddFlat(JsonElement sym, FileSymbols fs)
    {
        var name = sym.TryGetProperty("name", out JsonElement n) ? n.GetString() ?? "" : "";
        var kindNum = sym.TryGetProperty("kind", out JsonElement k) ? k.GetInt32() : 0;
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }
        var container = sym.TryGetProperty("containerName", out JsonElement c) ? c.GetString() ?? "" : "";
        if (IsTypeKind(kindNum))
        {
            var kindName = KindNames.TryGetValue(kindNum, out var kn) ? kn : "symbol";
            fs.Types.Add(new TypeSymbol(kindName, name));
        }
        else if (IsMemberKind(kindNum) && !string.IsNullOrEmpty(container))
        {
            var simpleContainer = container.Contains('.') ? container[(container.LastIndexOf('.') + 1)..] : container;
            TypeSymbol? owner = fs.Types.FirstOrDefault(t => string.Equals(t.Name, container, StringComparison.Ordinal))
                ?? fs.Types.FirstOrDefault(t => string.Equals(t.Name, simpleContainer, StringComparison.Ordinal));
            owner?.Members.Add(name);
        }
    }

    private static bool IsTypeKind(int kind) => kind is 5 or 10 or 11 or 22 or 23;
    private static bool IsMemberKind(int kind) => kind is 6 or 9 or 7 or 8 or 12 or 14;

    public async ValueTask DisposeAsync()
    {
        foreach (LspClient client in _clients.Values)
        {
            try
            {
                await client.DisposeAsync();
            }
            catch { }
        }
        _clients.Clear();
    }
}
