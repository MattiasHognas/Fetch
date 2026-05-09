using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Fetch.Core;

public sealed partial class LlmClient(AgentConfig config) : IDisposable
{
    private readonly HttpClient _http = new();
    private readonly AgentConfig _config = config;
    private bool? _supportsTokenizeEndpoint;

    [GeneratedRegex(@"<think>([\s\S]*?)</think>", RegexOptions.IgnoreCase)]
    private static partial Regex ThinkBlockRegex();

    public bool UsesChatCompletions => string.Equals(_config.ModelTransport, "chat", StringComparison.OrdinalIgnoreCase);
    public bool SupportsNativeToolCalling => UsesChatCompletions && _config.EnableNativeToolCalls;

    public async Task<string> ChatAsync(string prompt, bool stream = false) => (await CompletePromptAsync(prompt, stream)).Content;

    /// <summary>Removes &lt;think&gt;...&lt;/think&gt; reasoning blocks emitted by qwen3/deepseek-r1/gpt-oss before JSON parsing.</summary>
    public static string StripThinking(string response) =>
        string.IsNullOrEmpty(response) ? response : ThinkBlockRegex().Replace(response, string.Empty).Trim();

    public static bool IsThinkingModel(string? modelName)
    {
        if (string.IsNullOrEmpty(modelName))
        {
            return false;
        }
        var n = modelName.ToLowerInvariant();
        return n.StartsWith("qwen3", StringComparison.Ordinal)
            || n.StartsWith("deepseek-r1", StringComparison.Ordinal)
            || n.StartsWith("gpt-oss", StringComparison.Ordinal)
            || n.Contains(":qwen3", StringComparison.Ordinal)
            || n.Contains(":deepseek-r1", StringComparison.Ordinal);
    }

    public async Task<LlmPromptResponse> CompletePromptAsync(string prompt, bool stream = false) =>
        UsesChatCompletions
            ? stream ? await ChatPromptStreamingAsync(prompt) : await ChatPromptNonStreamingAsync(prompt)
            : stream ? await GeneratePromptStreamingAsync(prompt) : await GeneratePromptNonStreamingAsync(prompt);

    public async Task<LlmChatResponse> ChatWithToolsAsync(IReadOnlyList<LlmChatMessage> messages, IEnumerable<NativeToolDefinition> tools)
    {
        using HttpResponseMessage response = await _http.PostAsJsonAsync($"{_config.ModelBaseUrl.TrimEnd('/')}/api/chat", BuildChatRequest(messages, stream: false, tools));
        _ = response.EnsureSuccessStatusCode();
        using JsonDocument json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        return ParseChatResponse(json.RootElement);
    }

    private bool ShouldSendThink => _config.EnableThinking && IsThinkingModel(_config.ModelName);

    public async Task<int> CountPromptTokensAsync(string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return 0;
        }

        if (_supportsTokenizeEndpoint != false)
        {
            try
            {
                var count = await CountPromptTokensWithOllamaAsync(prompt);
                _supportsTokenizeEndpoint = true;
                return count;
            }
            catch (Exception)
            {
                _supportsTokenizeEndpoint = false;
            }
        }

        return EstimatePromptTokens(prompt);
    }

    private Dictionary<string, object?> BuildOptions()
    {
        var options = new Dictionary<string, object?>
        {
            ["num_ctx"] = _config.ContextWindowTokens,
            ["temperature"] = _config.Temperature
        };
        if (_config.ProviderPreserveThinking.HasValue)
        {
            options["preserve_thinking"] = _config.ProviderPreserveThinking.Value;
        }
        return options;
    }

    private Dictionary<string, object?> BuildGenerateRequest(string prompt, bool stream)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = _config.ModelName,
            ["prompt"] = prompt,
            ["stream"] = stream,
            ["options"] = BuildOptions()
        };
        if (ShouldSendThink)
        {
            payload["think"] = true;
        }
        return payload;
    }

    private Dictionary<string, object?> BuildChatRequest(IReadOnlyList<LlmChatMessage> messages, bool stream, IEnumerable<NativeToolDefinition>? tools = null)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = _config.ModelName,
            ["messages"] = messages.Select(ToChatMessagePayload).ToArray(),
            ["stream"] = stream,
            ["options"] = BuildOptions()
        };
        if (ShouldSendThink)
        {
            payload["think"] = true;
        }
        if (tools is not null)
        {
            payload["tools"] = tools.ToArray();
        }
        return payload;
    }

    private object ToChatMessagePayload(LlmChatMessage message)
    {
        var payload = new Dictionary<string, object?>
        {
            ["role"] = message.Role
        };
        if (message.Content is not null)
        {
            payload["content"] = message.Content;
        }
        if (_config.PreserveReasoning && !string.IsNullOrWhiteSpace(message.Thinking))
        {
            payload["thinking"] = message.Thinking;
        }
        if (!string.IsNullOrWhiteSpace(message.ToolName))
        {
            payload["tool_name"] = message.ToolName;
        }
        if (message.ToolCalls is { Count: > 0 })
        {
            payload["tool_calls"] = message.ToolCalls.Select(ToToolCallPayload).ToArray();
        }
        return payload;
    }

    private static object ToToolCallPayload(LlmToolCall toolCall)
    {
        object? arguments;
        try
        {
            arguments = JsonSerializer.Deserialize<object>(toolCall.ArgumentsJson);
        }
        catch
        {
            arguments = toolCall.ArgumentsJson;
        }

        return new Dictionary<string, object?>
        {
            ["function"] = new Dictionary<string, object?>
            {
                ["name"] = toolCall.Name,
                ["arguments"] = arguments
            }
        };
    }

    private async Task<int> CountPromptTokensWithOllamaAsync(string prompt)
    {
        using HttpResponseMessage response = await _http.PostAsJsonAsync($"{_config.ModelBaseUrl.TrimEnd('/')}/api/tokenize", new
        {
            model = _config.ModelName,
            prompt
        });
        _ = response.EnsureSuccessStatusCode();
        using JsonDocument json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        JsonElement root = json.RootElement;
        return root.TryGetProperty("tokens", out JsonElement tokens) && tokens.ValueKind == JsonValueKind.Array
            ? tokens.GetArrayLength()
            : root.TryGetProperty("token_ids", out JsonElement tokenIds) && tokenIds.ValueKind == JsonValueKind.Array
            ? tokenIds.GetArrayLength()
            : root.TryGetProperty("count", out JsonElement count) && count.TryGetInt32(out var parsedCount)
            ? parsedCount
            : root.TryGetProperty("prompt_eval_count", out JsonElement promptEvalCount) && promptEvalCount.TryGetInt32(out parsedCount)
            ? parsedCount
            : throw new InvalidOperationException("Ollama tokenize response did not include a token count.");
    }

    private static int EstimatePromptTokens(string prompt) => Math.Max(1, (prompt.Length + 3) / 4);

    private async Task<LlmPromptResponse> GeneratePromptNonStreamingAsync(string prompt)
    {
        HttpResponseMessage response = await _http.PostAsJsonAsync($"{_config.ModelBaseUrl.TrimEnd('/')}/api/generate", BuildGenerateRequest(prompt, false));
        _ = response.EnsureSuccessStatusCode();
        using JsonDocument json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        var raw = json.RootElement.TryGetProperty("response", out JsonElement r) ? r.GetString() ?? "" : json.RootElement.ToString();
        return ParseGenerateResponse(raw);
    }

    private async Task<LlmPromptResponse> GeneratePromptStreamingAsync(string prompt)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_config.ModelBaseUrl.TrimEnd('/')}/api/generate");
        request.Content = JsonContent.Create(BuildGenerateRequest(prompt, true));
        using HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        _ = response.EnsureSuccessStatusCode();
        await using Stream stream = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);
        var sb = new StringBuilder();
        string? line;
        while ((line = await reader.ReadLineAsync()) != null)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            using var doc = JsonDocument.Parse(line);
            if (doc.RootElement.TryGetProperty("response", out JsonElement r))
            {
                var token = r.GetString() ?? "";
                Console.Write(token);
                _ = sb.Append(token);
            }
            if (doc.RootElement.TryGetProperty("done", out JsonElement d) && d.GetBoolean())
            {
                break;
            }
        }
        Console.WriteLine();
        return ParseGenerateResponse(sb.ToString());
    }

    private async Task<LlmPromptResponse> ChatPromptNonStreamingAsync(string prompt)
    {
        using HttpResponseMessage response = await _http.PostAsJsonAsync($"{_config.ModelBaseUrl.TrimEnd('/')}/api/chat", BuildChatRequest([new LlmChatMessage("user", prompt)], stream: false));
        _ = response.EnsureSuccessStatusCode();
        using JsonDocument json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        LlmChatResponse parsed = ParseChatResponse(json.RootElement);
        return new LlmPromptResponse(parsed.Content, parsed.Reasoning);
    }

    private async Task<LlmPromptResponse> ChatPromptStreamingAsync(string prompt)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_config.ModelBaseUrl.TrimEnd('/')}/api/chat");
        request.Content = JsonContent.Create(BuildChatRequest([new LlmChatMessage("user", prompt)], stream: true));
        using HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        _ = response.EnsureSuccessStatusCode();
        await using Stream stream = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);
        var content = new StringBuilder();
        var reasoning = new StringBuilder();
        string? line;
        while ((line = await reader.ReadLineAsync()) != null)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            using var doc = JsonDocument.Parse(line);
            if (doc.RootElement.TryGetProperty("message", out JsonElement message))
            {
                if (message.TryGetProperty("thinking", out JsonElement thinking))
                {
                    _ = reasoning.Append(thinking.GetString() ?? "");
                }
                if (message.TryGetProperty("content", out JsonElement piece))
                {
                    var token = piece.GetString() ?? "";
                    Console.Write(token);
                    _ = content.Append(token);
                }
            }
            if (doc.RootElement.TryGetProperty("done", out JsonElement done) && done.GetBoolean())
            {
                break;
            }
        }
        Console.WriteLine();
        return new LlmPromptResponse(content.ToString().Trim(), reasoning.Length == 0 ? null : reasoning.ToString().Trim());
    }

    private static LlmPromptResponse ParseGenerateResponse(string raw)
    {
        var thinking = ExtractThinking(raw);
        return new LlmPromptResponse(StripThinking(raw), thinking);
    }

    private static string? ExtractThinking(string response)
    {
        if (string.IsNullOrWhiteSpace(response))
        {
            return null;
        }

        Match match = ThinkBlockRegex().Match(response);
        return match.Success && match.Groups.Count > 1
            ? match.Groups[1].Value.Trim()
            : null;
    }

    private static LlmChatResponse ParseChatResponse(JsonElement root)
    {
        if (!root.TryGetProperty("message", out JsonElement message))
        {
            return new LlmChatResponse(root.ToString(), null, []);
        }

        var content = message.TryGetProperty("content", out JsonElement c) ? c.GetString() ?? "" : "";
        var reasoning = message.TryGetProperty("thinking", out JsonElement t) ? t.GetString() ?? "" : "";
        var toolCalls = new List<LlmToolCall>();
        if (message.TryGetProperty("tool_calls", out JsonElement calls) && calls.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement call in calls.EnumerateArray())
            {
                if (!call.TryGetProperty("function", out JsonElement function))
                {
                    continue;
                }

                var name = function.TryGetProperty("name", out JsonElement nameElement) ? nameElement.GetString() ?? "" : "";
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                var argumentsJson = function.TryGetProperty("arguments", out JsonElement arguments)
                    ? arguments.GetRawText()
                    : "{}";
                toolCalls.Add(new LlmToolCall(name, argumentsJson));
            }
        }

        return new LlmChatResponse(content.Trim(), string.IsNullOrWhiteSpace(reasoning) ? null : reasoning.Trim(), toolCalls);
    }

    public void Dispose() => _http.Dispose();
}
