using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Fetch.Core;

public sealed partial class LlmClient(AgentConfig config) : IDisposable
{
    private readonly HttpClient _http = CreateHttpClient(config);
    private readonly AgentConfig _config = config;
    private bool? _supportsTokenizeEndpoint;

    private static HttpClient CreateHttpClient(AgentConfig config)
    {
        var http = new HttpClient
        {
            Timeout = config.ModelRequestTimeoutSeconds <= 0
                ? Timeout.InfiniteTimeSpan
                : TimeSpan.FromSeconds(config.ModelRequestTimeoutSeconds)
        };
        return http;
    }

    public Action<string>? PromptReasoningSink
    {
        get; set;
    }

    [GeneratedRegex(@"<think>([\s\S]*?)</think>", RegexOptions.IgnoreCase)]
    private static partial Regex ThinkBlockRegex();

    public async Task<string> ChatAsync(string prompt, bool stream = false) => (stream ? await ChatPromptStreamingAsync(prompt) : await ChatPromptNonStreamingAsync(prompt)).Content;

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

    public async Task<LlmChatResponse> ChatWithToolsAsync(IReadOnlyList<LlmChatMessage> messages, IEnumerable<NativeToolDefinition> tools)
    {
        var endpoint = $"{_config.ModelBaseUrl.TrimEnd('/')}/api/chat";

        using HttpResponseMessage response = await PostAsJsonAsync(endpoint, BuildChatRequest(messages, stream: false, tools));
        if (response.IsSuccessStatusCode)
        {
            return await ParseChatResponseAsync(response);
        }

        var initialFailure = await BuildHttpFailureMessageAsync(response, "/api/chat");
        if (response.StatusCode == HttpStatusCode.InternalServerError && CanRetryToolChatWithReducedState)
        {
            using HttpResponseMessage retryResponse = await PostAsJsonAsync(endpoint, BuildChatRequest(messages, stream: false, tools, sendThink: false, includeReasoning: false));
            if (retryResponse.IsSuccessStatusCode)
            {
                LlmChatResponse parsed = await ParseChatResponseAsync(retryResponse);
                return parsed with
                {
                    Warning = "Warning: the model retried once after a 500 from /api/chat without preserved reasoning/thinking. Output quality may be reduced."
                };
            }

            var retryFailure = await BuildHttpFailureMessageAsync(retryResponse, "/api/chat retry without thinking");
            throw new HttpRequestException($"{initialFailure}\nRetry without think/preserved reasoning also failed.\n{retryFailure}", null, retryResponse.StatusCode);
        }

        throw new HttpRequestException(initialFailure, null, response.StatusCode);
    }

    private bool ShouldSendThink => _config.EnableThinking && IsThinkingModel(_config.ModelName);
    private bool CanRetryToolChatWithReducedState => ShouldSendThink || _config.PreserveReasoning;

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

    private Dictionary<string, object?> BuildChatRequest(IReadOnlyList<LlmChatMessage> messages, bool stream, IEnumerable<NativeToolDefinition>? tools = null, bool? sendThink = null, bool includeReasoning = true)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = _config.ModelName,
            ["messages"] = messages.Select(message => ToChatMessagePayload(message, includeReasoning)).ToArray(),
            ["stream"] = stream,
            ["options"] = BuildOptions()
        };
        if (sendThink ?? ShouldSendThink)
        {
            payload["think"] = true;
        }
        if (tools is not null)
        {
            payload["tools"] = tools.ToArray();
        }
        return payload;
    }

    private Dictionary<string, object?> ToChatMessagePayload(LlmChatMessage message, bool includeReasoning)
    {
        var payload = new Dictionary<string, object?>
        {
            ["role"] = message.Role
        };
        if (message.Content is not null)
        {
            payload["content"] = message.Content;
        }
        if (includeReasoning && _config.PreserveReasoning && !string.IsNullOrWhiteSpace(message.Thinking))
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
        using HttpResponseMessage response = await PostAsJsonAsync($"{_config.ModelBaseUrl.TrimEnd('/')}/api/tokenize", new
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

    private async Task<LlmPromptResponse> ChatPromptNonStreamingAsync(string prompt)
    {
        using HttpResponseMessage response = await PostAsJsonAsync($"{_config.ModelBaseUrl.TrimEnd('/')}/api/chat", BuildChatRequest([new LlmChatMessage("user", prompt)], stream: false));
        await EnsureSuccessOrThrowAsync(response, "/api/chat");
        LlmChatResponse parsed = await ParseChatResponseAsync(response);
        EmitPromptReasoning(parsed.Reasoning);
        return new LlmPromptResponse(parsed.Content, parsed.Reasoning);
    }

    private async Task<LlmPromptResponse> ChatPromptStreamingAsync(string prompt)
    {
        var endpoint = $"{_config.ModelBaseUrl.TrimEnd('/')}/api/chat";
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_config.ModelBaseUrl.TrimEnd('/')}/api/chat");
        request.Content = JsonContent.Create(BuildChatRequest([new LlmChatMessage("user", prompt)], stream: true));
        using HttpResponseMessage response = await SendAsync(request, endpoint, HttpCompletionOption.ResponseHeadersRead);
        await EnsureSuccessOrThrowAsync(response, "/api/chat");
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
        var parsedReasoning = reasoning.Length == 0 ? null : reasoning.ToString().Trim();
        EmitPromptReasoning(parsedReasoning);
        return new LlmPromptResponse(content.ToString().Trim(), parsedReasoning);
    }

    private void EmitPromptReasoning(string? reasoning)
    {
        if (!string.IsNullOrWhiteSpace(reasoning))
        {
            PromptReasoningSink?.Invoke(reasoning);
        }
    }

    private Task<HttpResponseMessage> PostAsJsonAsync(string endpoint, object payload) => SendAsync(() => _http.PostAsJsonAsync(endpoint, payload), endpoint);

    private Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, string endpoint, HttpCompletionOption completionOption) => SendAsync(() => _http.SendAsync(request, completionOption), endpoint);

    private async Task<HttpResponseMessage> SendAsync(Func<Task<HttpResponseMessage>> send, string endpoint)
    {
        try
        {
            return await send();
        }
        catch (TaskCanceledException ex) when (_http.Timeout != Timeout.InfiniteTimeSpan)
        {
            var timeoutSeconds = (int)Math.Ceiling(_http.Timeout.TotalSeconds);
            throw new TimeoutException($"Model request to {endpoint} exceeded the configured timeout of {timeoutSeconds} seconds for model '{_config.ModelName}'. Increase ModelRequestTimeoutSeconds in .agent/config.json, or reduce thinking/preserved reasoning for slower models.", ex);
        }
    }

    private static async Task<LlmChatResponse> ParseChatResponseAsync(HttpResponseMessage response)
    {
        using JsonDocument json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        return ParseChatResponse(json.RootElement);
    }

    private static async Task EnsureSuccessOrThrowAsync(HttpResponseMessage response, string endpoint)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var message = await BuildHttpFailureMessageAsync(response, endpoint);
        throw new HttpRequestException(message, null, response.StatusCode);
    }

    private static async Task<string> BuildHttpFailureMessageAsync(HttpResponseMessage response, string endpoint)
    {
        var status = (int)response.StatusCode;
        var reason = string.IsNullOrWhiteSpace(response.ReasonPhrase) ? response.StatusCode.ToString() : response.ReasonPhrase;
        var body = response.Content is null ? "" : await response.Content.ReadAsStringAsync();
        var detail = SummarizeResponseBody(body);
        return $"Model request to {endpoint} failed with {status} ({reason}). Response body: {detail}";
    }

    private static string SummarizeResponseBody(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return "(empty body)";
        }

        var trimmed = body.Trim();
        const int maxChars = 1200;
        return trimmed.Length <= maxChars ? trimmed : trimmed[..maxChars] + "\n[truncated]";
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
