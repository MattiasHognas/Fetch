using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Fetch.Lsp;

public sealed class LspClient : IAsyncDisposable
{
    private readonly Process _process; private readonly Stream _stdin; private readonly Stream _stdout; private int _nextId = 1;
    public LspClient(LspServerConfig config, PathSandbox sandbox)
    {
        var psi = new ProcessStartInfo { FileName = config.Command, Arguments = string.Join(" ", config.Args), WorkingDirectory = sandbox.Root, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        _process = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start LSP server: {config.Command}");
        _stdin = _process.StandardInput.BaseStream;
        _stdout = _process.StandardOutput.BaseStream;
    }
    public async Task<JsonElement> RequestAsync(string method, object? parameters, CancellationToken ct = default)
    {
        var id = Interlocked.Increment(ref _nextId);
        var payload = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id,
            method,
            @params = parameters
        });
        await WriteMessageAsync(payload, ct);
        while (true)
        {
            var response = await ReadMessageAsync(ct);
            using var doc = JsonDocument.Parse(response);
            JsonElement root = doc.RootElement;
            if (!root.TryGetProperty("id", out JsonElement rid))
            {
                continue;
            }

            if (rid.GetInt32() != id)
            {
                continue;
            }

            return root.TryGetProperty("error", out JsonElement err)
                ? throw new InvalidOperationException(err.ToString())
                : root.GetProperty("result").Clone();
        }
    }
    public async Task NotifyAsync(string method, object? parameters, CancellationToken ct = default)
    {
        var payload = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            method,
            @params = parameters
        });
        await WriteMessageAsync(payload, ct);
    }
    private async Task WriteMessageAsync(string json, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        var header = Encoding.ASCII.GetBytes($"Content-Length: {bytes.Length}\r\n\r\n");
        await _stdin.WriteAsync(header, ct);
        await _stdin.WriteAsync(bytes, ct);
        await _stdin.FlushAsync(ct);
    }
    private async Task<string> ReadMessageAsync(CancellationToken ct)
    {
        var headers = new List<string>();
        while (true)
        {
            var line = await ReadAsciiLineAsync(ct);
            if (line == "")
            {
                break;
            }

            headers.Add(line);
        }
        var h = headers.FirstOrDefault(x => x.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidOperationException("LSP response missing Content-Length.");
        var len = int.Parse(h.Split(':', 2)[1].Trim(), System.Globalization.CultureInfo.InvariantCulture);
        var buffer = new byte[len];
        var read = 0;
        while (read < len)
        {
            read += await _stdout.ReadAsync(buffer.AsMemory(read, len - read), ct);
        }

        return Encoding.UTF8.GetString(buffer);
    }
    private async Task<string> ReadAsciiLineAsync(CancellationToken ct)
    {
        var bytes = new List<byte>();
        while (true)
        {
            var b = new byte[1];
            var read = await _stdout.ReadAsync(b, ct);
            if (read == 0)
            {
                throw new EndOfStreamException();
            }

            if (b[0] == '\n')
            {
                break;
            }

            if (b[0] != '\r')
            {
                bytes.Add(b[0]);
            }
        }
        return Encoding.ASCII.GetString([.. bytes]);
    }
    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(true);
            }
        }
        catch { }
        await Task.CompletedTask;
    }
}
