using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Fetch.Core;

public sealed class LlmClient(AgentConfig config) : IDisposable
{
    private readonly HttpClient _http = new();
    private readonly AgentConfig _config = config;
    private bool? _supportsTokenizeEndpoint;

    public Task<string> ChatAsync(string prompt, bool stream = false) => stream ? ChatStreamingAsync(prompt) : ChatNonStreamingAsync(prompt);

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
            catch (Exception) when (_supportsTokenizeEndpoint is null)
            {
                _supportsTokenizeEndpoint = false;
            }
        }

        return EstimatePromptTokens(prompt);
    }

    private object OllamaRequest(string prompt, bool stream) => new
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
        return json.RootElement.TryGetProperty("response", out JsonElement r) ? r.GetString() ?? "" : json.RootElement.ToString();
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
