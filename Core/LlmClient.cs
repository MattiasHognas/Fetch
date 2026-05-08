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

    [GeneratedRegex(@"<think>[\s\S]*?</think>", RegexOptions.IgnoreCase)]
    private static partial Regex ThinkBlockRegex();

    public Task<string> ChatAsync(string prompt, bool stream = false) => stream ? ChatStreamingAsync(prompt) : ChatNonStreamingAsync(prompt);

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

    private object OllamaRequest(string prompt, bool stream) => ShouldSendThink
        ? new
        {
            model = _config.ModelName,
            prompt,
            stream,
            think = true,
            options = new
            {
                num_ctx = _config.ContextWindowTokens
            }
        }
        : new
        {
            model = _config.ModelName,
            prompt,
            stream,
            options = new
            {
                num_ctx = _config.ContextWindowTokens
            }
        };

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

    private async Task<string> ChatNonStreamingAsync(string prompt)
    {
        HttpResponseMessage response = await _http.PostAsJsonAsync($"{_config.ModelBaseUrl.TrimEnd('/')}/api/generate", OllamaRequest(prompt, false));
        _ = response.EnsureSuccessStatusCode();
        using JsonDocument json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        var raw = json.RootElement.TryGetProperty("response", out JsonElement r) ? r.GetString() ?? "" : json.RootElement.ToString();
        return StripThinking(raw);
    }

    private async Task<string> ChatStreamingAsync(string prompt)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_config.ModelBaseUrl.TrimEnd('/')}/api/generate");
        request.Content = JsonContent.Create(OllamaRequest(prompt, true));
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
        return sb.ToString();
    }

    public void Dispose() => _http.Dispose();
}
