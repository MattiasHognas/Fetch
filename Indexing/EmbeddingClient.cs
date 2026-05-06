using System.Net.Http.Json;
using System.Text.Json;

namespace Fetch.Indexing;

public sealed class EmbeddingClient(AgentConfig config) : IDisposable
{
    private readonly HttpClient _http = new(); private readonly AgentConfig _config = config;

    public void Dispose() => _http.Dispose();
    public async Task<float[]> EmbedAsync(string text)
    {
        HttpResponseMessage res = await _http.PostAsJsonAsync($"{_config.EmbeddingBaseUrl.TrimEnd('/')}/api/embeddings", new
        {
            model = _config.EmbeddingModel,
            prompt = text
        });
        _ = res.EnsureSuccessStatusCode();
        using JsonDocument json = await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync());
        return [.. json.RootElement.GetProperty("embedding").EnumerateArray().Select(x => x.GetSingle())];
    }
}
