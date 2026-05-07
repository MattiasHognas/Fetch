using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Fetch.Lsp;

public sealed record RelationshipMapRequest(string[] Files, int MaxMethodsPerFile = 4, int MaxOutgoingCalls = 6);

public sealed partial class RelationshipMapTool(AgentConfig config, PathSandbox sandbox, LspServerSelector selector) : ITool
{
    private readonly AgentConfig _config = config;
    private readonly PathSandbox _sandbox = sandbox;
    private readonly LspServerSelector _selector = selector;

    public string Name => "relationship_map";
    public string Description => "Map how selected classes fit together. Uses structural parsing plus LSP call hierarchy when available. Input JSON {\"files\":[\"Program.cs\",\"Core/AgentLoop.cs\"]}.";
    public ApprovalMode Approval => ApprovalMode.Auto;

    public async Task<string> RunAsync(string input)
    {
        RelationshipMapRequest? request = ParseInput(input);
        if (request is null || request.Files.Length == 0)
        {
            return "Invalid input. Use JSON like {\"files\":[\"Program.cs\",\"Core/AgentLoop.cs\"]}.";
        }

        List<RelationshipFileModel> files = await LoadFilesAsync(request.Files);
        if (files.Count == 0)
        {
            return "relationship_map: no readable source files found.";
        }

        List<RelationshipEdge> edges = BuildStructuralEdges(files);
        List<RelationshipEdge> callEdges = await TryGetCallHierarchyEdgesAsync(files, request.MaxMethodsPerFile, request.MaxOutgoingCalls);
        edges.AddRange(callEdges);

        return edges.Count == 0 ? "relationship_map: no relationships found in the selected files." : Render(files, edges);
    }

    private async Task<List<RelationshipFileModel>> LoadFilesAsync(IEnumerable<string> requestedFiles)
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

            if (!File.Exists(absolutePath))
            {
                continue;
            }

            var text = await File.ReadAllTextAsync(absolutePath);
            files.Add(new RelationshipFileModel(relativePath, absolutePath, text, ParseTypes(text)));
        }

        return files;
    }

    private static List<RelationshipEdge> BuildStructuralEdges(List<RelationshipFileModel> files)
    {
        var edges = new List<RelationshipEdge>();
        HashSet<string> localTypeNames = new(files.SelectMany(file => file.Types.Select(type => type.Name)), StringComparer.Ordinal);
        foreach (RelationshipFileModel file in files)
        {
            foreach (RelationshipTypeModel type in file.Types)
            {
                foreach (var baseType in type.BaseTypes)
                {
                    if (ShouldIncludeType(baseType, localTypeNames))
                    {
                        edges.Add(new RelationshipEdge(type.Name, baseType, InferBaseRelation(baseType), file.RelativePath));
                    }
                }

                foreach (var dependency in type.ConstructorDependencies)
                {
                    if (ShouldIncludeType(dependency, localTypeNames))
                    {
                        edges.Add(new RelationshipEdge(type.Name, dependency, "injects", file.RelativePath));
                    }
                }

                foreach (var composed in type.ConstructedTypes)
                {
                    if (ShouldIncludeType(composed, localTypeNames))
                    {
                        edges.Add(new RelationshipEdge(type.Name, composed, "constructs", file.RelativePath));
                    }
                }
            }

            foreach (var constructed in ParseTopLevelConstructedTypes(file.Text))
            {
                if (ShouldIncludeType(constructed, localTypeNames))
                {
                    edges.Add(new RelationshipEdge(Path.GetFileNameWithoutExtension(file.RelativePath), constructed, "wires", file.RelativePath));
                }
            }
        }

        return Deduplicate(edges);
    }

    private async Task<List<RelationshipEdge>> TryGetCallHierarchyEdgesAsync(List<RelationshipFileModel> files, int maxMethodsPerFile, int maxOutgoingCalls)
    {
        if (files.Count == 0 || files.Any(f => !f.RelativePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)))
        {
            return [];
        }

        LspServerConfig? server = _selector.SelectForRepo();
        if (server is null || !string.Equals(server.Language, "csharp", StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        var edges = new List<RelationshipEdge>();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(15, _config.Lsp.RequestTimeoutSeconds * 2)));
            await using var client = new LspClient(server, _sandbox);
            await InitializeAsync(client, cts.Token);

            foreach (RelationshipFileModel file in files)
            {
                await client.NotifyAsync("textDocument/didOpen", new
                {
                    textDocument = new
                    {
                        uri = new Uri(file.AbsolutePath).AbsoluteUri,
                        languageId = server.Language,
                        version = 1,
                        text = file.Text
                    }
                }, cts.Token);

                JsonElement documentSymbols = await client.RequestAsync("textDocument/documentSymbol", new
                {
                    textDocument = new
                    {
                        uri = new Uri(file.AbsolutePath).AbsoluteUri
                    }
                }, cts.Token);

                foreach (RelationshipMethodModel method in ExtractMethodCandidates(documentSymbols).Take(maxMethodsPerFile))
                {
                    JsonElement prepared = await client.RequestAsync("textDocument/prepareCallHierarchy", new
                    {
                        textDocument = new
                        {
                            uri = new Uri(file.AbsolutePath).AbsoluteUri
                        },
                        position = new
                        {
                            line = Math.Max(0, method.Line - 1),
                            character = Math.Max(0, method.Character - 1)
                        }
                    }, cts.Token);

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
                            var uri = to.TryGetProperty("uri", out JsonElement uriElement) ? uriElement.GetString() : null;
                            var target = string.IsNullOrWhiteSpace(detail) ? targetName ?? "(unknown)" : $"{detail}.{targetName}";
                            if (string.IsNullOrWhiteSpace(targetName) || string.IsNullOrWhiteSpace(uri))
                            {
                                continue;
                            }

                            Uri? parsed = Uri.TryCreate(uri, UriKind.Absolute, out Uri? value) ? value : null;
                            if (parsed is null || !parsed.LocalPath.StartsWith(_sandbox.Root, StringComparison.Ordinal))
                            {
                                continue;
                            }

                            edges.Add(new RelationshipEdge(method.Owner + "." + method.Name, target, "calls", _sandbox.Relative(parsed.LocalPath)));
                        }
                    }
                }
            }
        }
        catch
        {
            return [];
        }

        return Deduplicate(edges);
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
                        hierarchicalDocumentSymbolSupport = true
                    },
                    callHierarchy = new
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

                var maxMethods = root.TryGetProperty("maxMethodsPerFile", out JsonElement methodsElement) && methodsElement.ValueKind == JsonValueKind.Number
                    ? Math.Clamp(methodsElement.GetInt32(), 1, 8)
                    : 4;
                var maxCalls = root.TryGetProperty("maxOutgoingCalls", out JsonElement callsElement) && callsElement.ValueKind == JsonValueKind.Number
                    ? Math.Clamp(callsElement.GetInt32(), 1, 12)
                    : 6;
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
        catch
        {
        }

        return new RelationshipMapRequest([input.Trim()]);
    }

    private static List<RelationshipTypeModel> ParseTypes(string text)
    {
        var models = new List<RelationshipTypeModel>();
        foreach (Match match in TypeRegex().Matches(text))
        {
            var typeName = match.Groups[2].Value.Trim();
            var baseList = match.Groups[3].Value.Trim();
            var body = ExtractTypeBody(text, match.Index + match.Length);
            List<string> baseTypes = string.IsNullOrWhiteSpace(baseList)
                ? []
                : [.. baseList.Split(',').Select(NormalizeTypeName).Where(static item => !string.IsNullOrWhiteSpace(item))];
            List<string> dependencies = ParseConstructorDependencies(typeName, body);
            List<string> constructed = ParseConstructedTypes(body);
            models.Add(new RelationshipTypeModel(typeName, baseTypes, dependencies, constructed));
        }

        return models;
    }

    private static string ExtractTypeBody(string text, int startIndex)
    {
        var braceIndex = text.IndexOf('{', startIndex - 1);
        if (braceIndex < 0)
        {
            return "";
        }

        var depth = 0;
        for (var index = braceIndex; index < text.Length; index++)
        {
            if (text[index] == '{')
            {
                depth++;
            }
            else if (text[index] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return text[(braceIndex + 1)..index];
                }
            }
        }

        return text[(braceIndex + 1)..];
    }

    private static List<string> ParseConstructorDependencies(string typeName, string body)
    {
        var dependencies = new List<string>();
        Match constructorMatch = Regex.Match(body, "\\b" + Regex.Escape(typeName) + "\\s*\\(([^)]*)\\)", RegexOptions.Multiline);
        if (!constructorMatch.Success)
        {
            return dependencies;
        }

        foreach (var parameter in constructorMatch.Groups[1].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var normalized = NormalizeParameterType(parameter);
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                dependencies.Add(normalized);
            }
        }

        return dependencies;
    }

    private static List<string> ParseConstructedTypes(string body)
    {
        var types = new List<string>();
        foreach (Match match in NewExpressionRegex().Matches(body))
        {
            var normalized = NormalizeTypeName(match.Groups[1].Value);
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                types.Add(normalized);
            }
        }

        return types;
    }

    private static List<string> ParseTopLevelConstructedTypes(string text)
    {
        var types = new List<string>();
        foreach (Match match in NewExpressionRegex().Matches(text))
        {
            var normalized = NormalizeTypeName(match.Groups[1].Value);
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                types.Add(normalized);
            }
        }

        return types;
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
        const int methodKind = 6;
        const int constructorKind = 9;
        const int classKind = 5;
        const int structKind = 23;
        const int interfaceKind = 11;
        const int recordKind = 23;

        var name = symbol.TryGetProperty("name", out JsonElement nameElement) ? nameElement.GetString() ?? "" : "";
        var kind = symbol.TryGetProperty("kind", out JsonElement kindElement) ? kindElement.GetInt32() : 0;
        var nextOwner = kind is classKind or structKind or interfaceKind or recordKind ? name : owner;

        if (kind is methodKind or constructorKind)
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

    private static string InferBaseRelation(string typeName)
    {
        return typeName.StartsWith('I') && typeName.Length > 1 && char.IsUpper(typeName[1])
            ? "implements"
            : "inherits";
    }

    private static string NormalizeParameterType(string parameter)
    {
        var withoutDefault = parameter.Split('=')[0].Trim();
        var tokens = withoutDefault.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 2)
        {
            return "";
        }

        var type = string.Join(" ", tokens.Take(tokens.Length - 1));
        return NormalizeTypeName(type.Replace("params ", "", StringComparison.Ordinal).Replace("this ", "", StringComparison.Ordinal));
    }

    private static string NormalizeTypeName(string raw)
    {
        var normalized = raw.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return "";
        }

        normalized = normalized.Replace("?", "", StringComparison.Ordinal)
            .Replace("[]", "", StringComparison.Ordinal)
            .Replace("global::", "", StringComparison.Ordinal)
            .Trim();
        string[] modifiers = ["public", "private", "protected", "internal", "readonly", "static", "sealed", "partial", "ref", "out", "in", "required"];
        foreach (var modifier in modifiers)
        {
            if (normalized.StartsWith(modifier + " ", StringComparison.Ordinal))
            {
                normalized = normalized[(modifier.Length + 1)..].Trim();
            }
        }

        var genericStart = normalized.IndexOf('<');
        var genericEnd = normalized.LastIndexOf('>');
        if (genericStart > 0 && genericEnd > genericStart)
        {
            var outer = normalized[..genericStart].Trim();
            var inner = normalized[(genericStart + 1)..genericEnd].Trim();
            if (outer is "IEnumerable" or "IReadOnlyList" or "IList" or "List" or "HashSet" or "IReadOnlyCollection" or "Task" or "ValueTask" or "Option")
            {
                var firstInner = inner.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? inner;
                return NormalizeTypeName(firstInner);
            }
        }

        return normalized;
    }

    private static bool ShouldIncludeType(string typeName, HashSet<string> localTypeNames)
    {
        return !string.IsNullOrWhiteSpace(typeName) && (localTypeNames.Contains(typeName) || typeName is "ITool" || (!typeName.StartsWith("System.", StringComparison.Ordinal)
            && !typeName.StartsWith("Microsoft.", StringComparison.Ordinal)
            && typeName is not "RootCommand" and not "Command" and not "bool" and not "int" and not "string" and not "Task" && !char.IsLower(typeName[0]) && (typeName.EndsWith("Tool", StringComparison.Ordinal)
            || typeName.EndsWith("Store", StringComparison.Ordinal)
            || typeName.EndsWith("Policy", StringComparison.Ordinal)
            || typeName.EndsWith("Client", StringComparison.Ordinal)
            || typeName.EndsWith("Session", StringComparison.Ordinal)
            || typeName.EndsWith("Config", StringComparison.Ordinal)
            || typeName.EndsWith("RuntimeState", StringComparison.Ordinal)
            || typeName.EndsWith("Index", StringComparison.Ordinal)
            || typeName.EndsWith("Handler", StringComparison.Ordinal)
            || typeName.EndsWith("Planner", StringComparison.Ordinal)
            || typeName.EndsWith("Compactor", StringComparison.Ordinal)
            || typeName.EndsWith("Refiner", StringComparison.Ordinal)
            || typeName.EndsWith("Map", StringComparison.Ordinal))));
    }

    private static List<RelationshipEdge> Deduplicate(IEnumerable<RelationshipEdge> edges)
    {
        return [.. edges
            .Where(static edge => !string.IsNullOrWhiteSpace(edge.From) && !string.IsNullOrWhiteSpace(edge.To))
            .GroupBy(edge => edge.From + "|" + edge.Relation + "|" + edge.To + "|" + edge.SourceFile, StringComparer.Ordinal)
            .Select(group => group.First())];
    }

    private static string Render(List<RelationshipFileModel> files, List<RelationshipEdge> edges)
    {
        var structural = edges.Where(edge => edge.Relation is not "calls").ToList();
        var calls = edges.Where(edge => edge.Relation == "calls").ToList();
        var sb = new StringBuilder();
        _ = sb.Append("relationship_map: ").Append(files.Count).Append(" file(s)\n\n");
        _ = sb.Append("Files\n");
        foreach (RelationshipFileModel file in files)
        {
            _ = sb.Append("- ").Append(file.RelativePath).Append('\n');
        }

        _ = sb.Append("\nStructural edges\n");
        foreach (RelationshipEdge edge in structural.Take(80))
        {
            _ = sb.Append("- ").Append(edge.From).Append(" --").Append(edge.Relation).Append("--> ").Append(edge.To).Append(" [").Append(edge.SourceFile).Append("]\n");
        }

        if (calls.Count > 0)
        {
            _ = sb.Append("\nCall edges (LSP)\n");
            foreach (RelationshipEdge edge in calls.Take(80))
            {
                _ = sb.Append("- ").Append(edge.From).Append(" --calls--> ").Append(edge.To).Append(" [").Append(edge.SourceFile).Append("]\n");
            }
        }

        return sb.ToString().TrimEnd();
    }

    [GeneratedRegex(@"\b(class|record|interface|struct)\s+([A-Za-z_][A-Za-z0-9_]*)\s*(?::\s*([^\{\n]+))?", RegexOptions.Multiline)]
    private static partial Regex TypeRegex();

    [GeneratedRegex(@"new\s+([A-Z][A-Za-z0-9_<>\.,]*)\s*\(", RegexOptions.Multiline)]
    private static partial Regex NewExpressionRegex();
}

internal sealed record RelationshipFileModel(string RelativePath, string AbsolutePath, string Text, List<RelationshipTypeModel> Types);

internal sealed record RelationshipTypeModel(string Name, List<string> BaseTypes, List<string> ConstructorDependencies, List<string> ConstructedTypes);

internal sealed record RelationshipMethodModel(string Owner, string Name, int Line, int Character);

internal sealed record RelationshipEdge(string From, string To, string Relation, string SourceFile);