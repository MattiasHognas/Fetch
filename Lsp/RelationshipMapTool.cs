using System.Text;
using System.Text.Json;

namespace Fetch.Lsp;

public sealed record RelationshipMapRequest(string[] Files, int MaxMethodsPerFile = 4, int MaxOutgoingCalls = 6);

public sealed partial class RelationshipMapTool(AgentConfig config, PathSandbox sandbox, LspServerSelector selector) : ITool, INativeTool
{
    private readonly AgentConfig _config = config;
    private readonly PathSandbox _sandbox = sandbox;
    private readonly LspServerSelector _selector = selector;

    public string Name => "relationship_map";
    public string Description => "Map outgoing calls between methods in selected files via LSP call hierarchy from JSON arguments. Requires a configured C# LSP server.";
    public ApprovalMode Approval => ApprovalMode.Auto;

    public object GetParametersSchema() => NativeToolJson.ObjectSchema(new Dictionary<string, object?>
    {
        ["files"] = NativeToolJson.ArrayProperty(NativeToolJson.StringProperty("Repo-relative C# file path."), "Files to analyze.", 1),
        ["maxMethodsPerFile"] = NativeToolJson.IntegerProperty("Optional maximum methods per file to inspect.", 1),
        ["maxOutgoingCalls"] = NativeToolJson.IntegerProperty("Optional maximum outgoing calls per method.", 1)
    }, "files");

    public string ConvertArguments(JsonElement arguments)
    {
        return NativeToolJson.TryGetStringArray(arguments, "files", out var files)
            ? NativeToolJson.SerializeObject(new Dictionary<string, object?>
            {
                ["files"] = files,
                ["maxMethodsPerFile"] = NativeToolJson.TryGetInt(arguments, "maxMethodsPerFile", out var maxMethodsPerFile) ? maxMethodsPerFile : null,
                ["maxOutgoingCalls"] = NativeToolJson.TryGetInt(arguments, "maxOutgoingCalls", out var maxOutgoingCalls) ? maxOutgoingCalls : null
            })
            : "";
    }

    public async Task<string> RunAsync(string input)
    {
        RelationshipMapRequest? request = ParseInput(input);
        if (request is null || request.Files.Length == 0)
        {
            return "Invalid input. Use JSON like {\"files\":[\"Program.cs\",\"Core/AgentLoop.cs\"]}.";
        }

        List<RelationshipFileModel> files = LoadFiles(request.Files);
        if (files.Count == 0)
        {
            return "relationship_map: no readable source files found.";
        }
        if (files.Any(f => !f.RelativePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)))
        {
            return "relationship_map: currently only C# files are supported.";
        }

        LspServerConfig? server = _selector.SelectForRepo();
        if (server is null || !string.Equals(server.Language, "csharp", StringComparison.OrdinalIgnoreCase))
        {
            return "relationship_map: requires a configured LSP server for C#.";
        }

        List<RelationshipEdge> edges;
        try
        {
            edges = await CollectCallEdgesAsync(server, files, request.MaxMethodsPerFile, request.MaxOutgoingCalls);
        }
        catch (Exception ex)
        {
            return $"relationship_map: LSP server {server.Id} failed: {ex.Message}";
        }

        return edges.Count > 0
            ? Render(files, edges)
            : $"relationship_map: LSP server {server.Id} returned no call edges for the selected files.";
    }

    private List<RelationshipFileModel> LoadFiles(IEnumerable<string> requestedFiles)
    {
        var files = new List<RelationshipFileModel>();
        foreach (var requested in requestedFiles.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var relativePath = requested.Trim();
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                continue;
            }
            string absolutePath;
            try
            {
                absolutePath = _sandbox.Resolve(relativePath);
            }
            catch
            {
                continue;
            }
            if (!File.Exists(absolutePath) && TryResolveByLeafName(relativePath, out var leafAbs, out var leafRel))
            {
                absolutePath = leafAbs;
                relativePath = leafRel;
            }
            if (File.Exists(absolutePath))
            {
                files.Add(new RelationshipFileModel(relativePath, absolutePath));
            }
        }
        return files;
    }

    private bool TryResolveByLeafName(string requested, out string absolutePath, out string relativePath)
    {
        absolutePath = "";
        relativePath = "";
        var leaf = Path.GetFileName(requested);
        if (string.IsNullOrWhiteSpace(leaf) || leaf != requested.Replace('\\', '/').TrimStart('/'))
        {
            return false;
        }

        try
        {
            foreach (var candidate in Directory.EnumerateFiles(_sandbox.Root, leaf, SearchOption.AllDirectories))
            {
                var rel = _sandbox.Relative(candidate).Replace('\\', '/');
                if (rel.StartsWith(".git/", StringComparison.Ordinal)
                    || rel.StartsWith(".agent/", StringComparison.Ordinal)
                    || rel.StartsWith("bin/", StringComparison.Ordinal)
                    || rel.StartsWith("obj/", StringComparison.Ordinal)
                    || rel.StartsWith("node_modules/", StringComparison.Ordinal)
                    || rel.Contains("/bin/", StringComparison.Ordinal)
                    || rel.Contains("/obj/", StringComparison.Ordinal))
                {
                    continue;
                }
                absolutePath = candidate;
                relativePath = rel;
                return true;
            }
        }
        catch
        {
        }
        return false;
    }

    private async Task<List<RelationshipEdge>> CollectCallEdgesAsync(LspServerConfig server, List<RelationshipFileModel> files, int maxMethodsPerFile, int maxOutgoingCalls)
    {
        var edges = new List<RelationshipEdge>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(60, _config.Lsp.RequestTimeoutSeconds * 4)));
        await using var client = new LspClient(server, _sandbox);
        await InitializeAsync(client, cts.Token);

        var fileUris = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (RelationshipFileModel file in files)
        {
            var uri = new Uri(file.AbsolutePath).AbsoluteUri;
            fileUris[file.RelativePath] = uri;
            var text = await File.ReadAllTextAsync(file.AbsolutePath, cts.Token);
            await client.NotifyAsync("textDocument/didOpen", new
            {
                textDocument = new
                {
                    uri,
                    languageId = server.Language,
                    version = 1,
                    text
                }
            }, cts.Token);
        }

        foreach (RelationshipFileModel file in files)
        {
            var uri = fileUris[file.RelativePath];
            JsonElement documentSymbols = await client.RequestAsync("textDocument/documentSymbol", new
            {
                textDocument = new
                {
                    uri
                }
            }, cts.Token);

            foreach (RelationshipMethodModel method in ExtractMethodCandidates(documentSymbols).Take(maxMethodsPerFile))
            {
                JsonElement prepared = await PrepareCallHierarchyWithRetryAsync(client, uri, method, cts.Token);
                if (prepared.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (JsonElement item in prepared.EnumerateArray())
                {
                    JsonElement outgoing = await client.RequestAsync("callHierarchy/outgoingCalls", new
                    {
                        item
                    }, cts.Token);
                    if (outgoing.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }

                    foreach (JsonElement call in outgoing.EnumerateArray().Take(maxOutgoingCalls))
                    {
                        if (!call.TryGetProperty("to", out JsonElement to))
                        {
                            continue;
                        }
                        var targetName = to.TryGetProperty("name", out JsonElement nameElement) ? nameElement.GetString() : null;
                        var detail = to.TryGetProperty("detail", out JsonElement detailElement) ? detailElement.GetString() : null;
                        var targetUri = to.TryGetProperty("uri", out JsonElement uriElement) ? uriElement.GetString() : null;
                        if (string.IsNullOrWhiteSpace(targetName) || string.IsNullOrWhiteSpace(targetUri))
                        {
                            continue;
                        }
                        if (!Uri.TryCreate(targetUri, UriKind.Absolute, out Uri? parsed) || !parsed.LocalPath.StartsWith(_sandbox.Root, StringComparison.Ordinal))
                        {
                            continue;
                        }
                        var target = string.IsNullOrWhiteSpace(detail) ? targetName : $"{detail}.{targetName}";
                        edges.Add(new RelationshipEdge($"{method.Owner}.{method.Name}", target, _sandbox.Relative(parsed.LocalPath)));
                    }
                }
            }
        }

        return [.. edges
            .GroupBy(e => $"{e.From}|{e.To}|{e.SourceFile}", StringComparer.Ordinal)
            .Select(g => g.First())];
    }

    private static async Task<JsonElement> PrepareCallHierarchyWithRetryAsync(LspClient client, string uri, RelationshipMethodModel method, CancellationToken ct)
    {
        TimeSpan[] delays = [TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8)];
        JsonElement last = default;
        foreach (TimeSpan delay in delays)
        {
            if (delay > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(delay, ct);
                }
                catch { return last; }
            }
            try
            {
                last = await client.RequestAsync("textDocument/prepareCallHierarchy", new
                {
                    textDocument = new
                    {
                        uri
                    },
                    position = new
                    {
                        line = Math.Max(0, method.Line - 1),
                        character = Math.Max(0, method.Character - 1)
                    }
                }, ct);
            }
            catch
            {
                continue;
            }
            if (last.ValueKind == JsonValueKind.Array && last.GetArrayLength() > 0)
            {
                return last;
            }
        }
        return last;
    }

    private async Task InitializeAsync(LspClient client, CancellationToken ct)
    {
        var rootUri = new Uri(_sandbox.Root).AbsoluteUri;
        var folderName = new DirectoryInfo(_sandbox.Root).Name;
        _ = await client.RequestAsync("initialize", new
        {
            processId = Environment.ProcessId,
            rootUri,
            rootPath = _sandbox.Root,
            workspaceFolders = new[]
            {
                new { uri = rootUri, name = folderName }
            },
            capabilities = new
            {
                workspace = new
                {
                    workspaceFolders = true,
                    configuration = true
                },
                textDocument = new
                {
                    documentSymbol = new
                    {
                        hierarchicalDocumentSymbolSupport = true
                    },
                    callHierarchy = new
                    {
                        dynamicRegistration = false
                    }
                },
                window = new
                {
                    workDoneProgress = true
                }
            }
        }, ct);
        await client.NotifyAsync("initialized", new
        {
        }, ct);
    }

    private static RelationshipMapRequest? ParseInput(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }
        try
        {
            using var doc = JsonDocument.Parse(input);
            JsonElement root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                List<string> files = [];
                if (root.TryGetProperty("files", out JsonElement filesElement) && filesElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement item in filesElement.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                        {
                            files.Add(item.GetString()!);
                        }
                    }
                }
                else if (root.TryGetProperty("file", out JsonElement fileElement) && fileElement.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(fileElement.GetString()))
                {
                    files.Add(fileElement.GetString()!);
                }
                var maxMethods = root.TryGetProperty("maxMethodsPerFile", out JsonElement m) && m.ValueKind == JsonValueKind.Number ? Math.Clamp(m.GetInt32(), 1, 8) : 4;
                var maxCalls = root.TryGetProperty("maxOutgoingCalls", out JsonElement c) && c.ValueKind == JsonValueKind.Number ? Math.Clamp(c.GetInt32(), 1, 12) : 6;
                return new RelationshipMapRequest([.. files], maxMethods, maxCalls);
            }
            if (root.ValueKind == JsonValueKind.Array)
            {
                List<string> files = [];
                foreach (JsonElement item in root.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                    {
                        files.Add(item.GetString()!);
                    }
                }
                return new RelationshipMapRequest([.. files]);
            }
        }
        catch { }
        return new RelationshipMapRequest([input.Trim()]);
    }

    private static IEnumerable<RelationshipMethodModel> ExtractMethodCandidates(JsonElement symbols)
    {
        if (symbols.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }
        foreach (JsonElement symbol in symbols.EnumerateArray())
        {
            foreach (RelationshipMethodModel method in ExtractMethodCandidates(symbol, ""))
            {
                yield return method;
            }
        }
    }

    private static IEnumerable<RelationshipMethodModel> ExtractMethodCandidates(JsonElement symbol, string owner)
    {
        var name = symbol.TryGetProperty("name", out JsonElement nameElement) ? nameElement.GetString() ?? "" : "";
        var kind = symbol.TryGetProperty("kind", out JsonElement kindElement) ? kindElement.GetInt32() : 0;
        var nextOwner = kind is 5 or 11 or 22 or 23 ? name : owner;

        if (kind is 6 or 9)
        {
            JsonElement start = symbol.GetProperty("selectionRange").GetProperty("start");
            yield return new RelationshipMethodModel(nextOwner, name, start.GetProperty("line").GetInt32() + 1, start.GetProperty("character").GetInt32() + 1);
        }

        if (symbol.TryGetProperty("children", out JsonElement children) && children.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement child in children.EnumerateArray())
            {
                foreach (RelationshipMethodModel method in ExtractMethodCandidates(child, nextOwner))
                {
                    yield return method;
                }
            }
        }
    }

    private static string Render(List<RelationshipFileModel> files, List<RelationshipEdge> edges)
    {
        var sb = new StringBuilder();
        _ = sb.Append("relationship_map: ").Append(files.Count).Append(" file(s)\n\nFiles\n");
        foreach (RelationshipFileModel file in files)
        {
            _ = sb.Append("- ").Append(file.RelativePath).Append('\n');
        }
        _ = sb.Append("\nCall edges (LSP)\n");
        foreach (RelationshipEdge edge in edges.Take(80))
        {
            _ = sb.Append("- ").Append(edge.From).Append(" --calls--> ").Append(edge.To).Append(" [").Append(edge.SourceFile).Append("]\n");
        }
        return sb.ToString().TrimEnd();
    }
}

internal sealed record RelationshipFileModel(string RelativePath, string AbsolutePath);
internal sealed record RelationshipMethodModel(string Owner, string Name, int Line, int Character);
internal sealed record RelationshipEdge(string From, string To, string SourceFile);
