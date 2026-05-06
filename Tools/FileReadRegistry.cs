using System.Security.Cryptography;
using System.Text;

namespace Fetch.Tools;

public sealed class FileReadRegistry
{
    private readonly Dictionary<string, string> _hashes = [];
    public void MarkRead(string path, string content) => _hashes[Path.GetFullPath(path)] = Hash(content);
    public bool WasReadAndUnchanged(string path, string currentContent) => _hashes.TryGetValue(Path.GetFullPath(path), out var h) && h == Hash(currentContent);
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
