using System.Text.Json;

namespace Fetch.Lsp;

public sealed class LspSymbolSearchTool(AgentConfig config, PathSandbox sandbox, LspServerSelector selector, SearchContentTool fallback) : ITool, INativeTool
{
    private readonly AgentConfig _config = config; private readonly PathSandbox _sandbox = sandbox; private readonly LspServerSelector _selector = selector; private readonly SearchContentTool _fallback = fallback;

    public string Name => "symbol_search";
    public string Description => "Search code symbols such as classes, functions, methods, interfaces from JSON arguments. Uses LSP if available, otherwise text search.";
    public ApprovalMode Approval => ApprovalMode.Auto;

    public object GetParametersSchema() => NativeToolJson.ObjectSchema(new Dictionary<string, object?>
    {
        ["query"] = NativeToolJson.StringProperty("Symbol query text.")
    }, "query");

    public string ConvertArguments(JsonElement arguments)
    {
        return NativeToolJson.TryGetString(arguments, "query", out var query)
            ? query
            : "";
    }

    public async Task<string> RunAsync(string input)
    {
        LspServerConfig? server = _selector.SelectForRepo();
        if (server is null)
        {
            return await Fallback(input, "No configured LSP server available.");
        }

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(_config.Lsp.RequestTimeoutSeconds));
            await using var client = new LspClient(server, _sandbox);
            await InitializeAsync(client, cts.Token);
            JsonElement result = await client.RequestAsync("workspace/symbol", new
            {
                query = input.Trim()
            }, cts.Token);
            var rendered = RenderSymbols(result);
            return string.IsNullOrWhiteSpace(rendered) ? await Fallback(input, $"LSP server {server.Id} returned no symbols.") : rendered;
        }
        catch (Exception ex) { return await Fallback(input, $"LSP symbol search failed: {ex.Message}"); }
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
                workspace = new
                {
                    symbol = new
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
    private static string RenderSymbols(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Array)
        {
            return "";
        }

        var lines = new List<string>();
        foreach (JsonElement sym in result.EnumerateArray().Take(50))
        {
            var name = sym.TryGetProperty("name", out JsonElement n) ? n.GetString() : "";
            var kind = sym.TryGetProperty("kind", out JsonElement k) ? k.GetInt32() : 0;
            if (!sym.TryGetProperty("location", out JsonElement loc))
            {
                continue;
            }

            var uri = loc.TryGetProperty("uri", out JsonElement u) ? u.GetString() : "";
            JsonElement start = loc.GetProperty("range").GetProperty("start");
            var line = start.GetProperty("line").GetInt32() + 1;
            var ch = start.GetProperty("character").GetInt32() + 1;
            var path = Uri.TryCreate(uri, UriKind.Absolute, out Uri? parsed) ? parsed.LocalPath : uri;
            lines.Add($"{name} kind={kind} {path}:{line}:{ch}");
        }
        return string.Join("\n", lines);
    }
    private async Task<string> Fallback(string input, string reason) => !_config.Lsp.FallbackToTextSearch ? reason : $"{reason}\n\nFallback search:\n{await _fallback.RunAsync(input)}";
}

public sealed record ReferenceRequest(string File, int Line, int Character, string Symbol);

public sealed class LspReferencesSearchTool(AgentConfig config, PathSandbox sandbox, LspServerSelector selector, SearchContentTool fallback) : ITool, INativeTool
{
    private readonly AgentConfig _config = config; private readonly PathSandbox _sandbox = sandbox; private readonly LspServerSelector _selector = selector; private readonly SearchContentTool _fallback = fallback;

    public string Name => "references_search";
    public string Description => "Find references or usages of a symbol from JSON arguments. Uses LSP if available, otherwise text search.";
    public ApprovalMode Approval => ApprovalMode.Auto;

    public object GetParametersSchema() => NativeToolJson.ObjectSchema(new Dictionary<string, object?>
    {
        ["file"] = NativeToolJson.StringProperty("Repo-relative file path containing the symbol."),
        ["line"] = NativeToolJson.IntegerProperty("1-based line number of the symbol reference.", 1),
        ["character"] = NativeToolJson.IntegerProperty("1-based character position of the symbol reference.", 1),
        ["symbol"] = NativeToolJson.StringProperty("Exact symbol name.")
    }, "file", "line", "character", "symbol");

    public string ConvertArguments(JsonElement arguments)
    {
        return NativeToolJson.TryGetString(arguments, "file", out var file)
            && NativeToolJson.TryGetInt(arguments, "line", out var line)
            && NativeToolJson.TryGetInt(arguments, "character", out var character)
            && NativeToolJson.TryGetString(arguments, "symbol", out var symbol)
            ? NativeToolJson.SerializeObject(new Dictionary<string, object?>
            {
                ["file"] = file,
                ["line"] = line,
                ["character"] = character,
                ["symbol"] = symbol
            })
            : "";
    }

    public async Task<string> RunAsync(string input)
    {
        ReferenceRequest? req = ParseInput(input);
        if (req is null)
        {
            return "Invalid input. Use JSON: {\"file\":\"src/Foo.cs\",\"line\":10,\"character\":5,\"symbol\":\"Foo\"}";
        }

        LspServerConfig? server = _selector.SelectForRepo();
        if (server is null)
        {
            return await Fallback(req.Symbol, "No configured LSP server available.");
        }

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(_config.Lsp.RequestTimeoutSeconds));
            await using var client = new LspClient(server, _sandbox);
            await InitializeAsync(client, cts.Token);
            var uri = new Uri(_sandbox.Resolve(req.File)).AbsoluteUri;
            JsonElement result = await client.RequestAsync("textDocument/references", new
            {
                textDocument = new
                {
                    uri
                },
                position = new
                {
                    line = Math.Max(0, req.Line - 1),
                    character = Math.Max(0, req.Character - 1)
                },
                context = new
                {
                    includeDeclaration = true
                }
            }, cts.Token);
            var rendered = RenderReferences(result);
            return string.IsNullOrWhiteSpace(rendered) ? await Fallback(req.Symbol, $"LSP server {server.Id} returned no references.") : rendered;
        }
        catch (Exception ex) { return await Fallback(req.Symbol, $"LSP references search failed: {ex.Message}"); }
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
                    references = new
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
    private static ReferenceRequest? ParseInput(string input)
    {
        try
        {
            return JsonSerializer.Deserialize<ReferenceRequest>(input, AgentConfig.JsonOptions());
        }
        catch { return null; }
    }
    private static string RenderReferences(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Array)
        {
            return "";
        }

        var lines = new List<string>();
        foreach (JsonElement item in result.EnumerateArray().Take(100))
        {
            var uri = item.GetProperty("uri").GetString() ?? "";
            JsonElement start = item.GetProperty("range").GetProperty("start");
            var line = start.GetProperty("line").GetInt32() + 1;
            var ch = start.GetProperty("character").GetInt32() + 1;
            var path = Uri.TryCreate(uri, UriKind.Absolute, out Uri? parsed) ? parsed.LocalPath : uri;
            lines.Add($"{path}:{line}:{ch}");
        }
        return string.Join("\n", lines);
    }
    private async Task<string> Fallback(string symbol, string reason) => !_config.Lsp.FallbackToTextSearch ? reason : $"{reason}\n\nFallback search:\n{await _fallback.RunAsync(symbol)}";
}
