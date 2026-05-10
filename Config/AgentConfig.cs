using System.Text.Json;

namespace Fetch.Config;

public sealed class AgentConfig
{
    public string ConfigPath { get; set; } = "";
    public string ModelBaseUrl { get; set; } = "http://localhost:11434";
    public string ModelName { get; set; } = "qwen3.6:35b";
    public bool PreserveReasoning { get; set; } = true;
    public bool? ProviderPreserveThinking
    {
        get; set;
    }
    public double Temperature
    {
        get; set;
    }
    public int ContextWindowTokens { get; set; } = 100000;
    public int ContextWindowReserveTokens { get; set; } = 8192;
    public string EmbeddingBaseUrl { get; set; } = "http://localhost:11434";
    public string EmbeddingModel { get; set; } = "nomic-embed-text";
    public string ApprovalMode { get; set; } = "ask";
    public int MaxAgentSteps { get; set; } = 100;
    public int MaxFailedCommandAttempts { get; set; } = 4;
    public bool EnableThinking { get; set; } = true;
    public int ModelRequestTimeoutSeconds { get; set; } = 600;
    public int MaxTriageTokens { get; set; } = 512;
    public int MaxStepsPerPhase { get; set; } = 25;
    public bool AutoBuildSemanticIndexOnAgentRun { get; set; } = true;
    public int SemanticIndexBuildTimeoutSeconds { get; set; } = 30;
    public bool AutoReindex { get; set; } = true;
    public int DefaultCommandTimeoutSeconds { get; set; } = 60;
    public int MaxCommandTimeoutSeconds { get; set; } = 600;
    public int MaxToolResultChars { get; set; } = 40000;
    public int MaxToolResultTokens { get; set; } = 10000;
    public int MaxRecentStateChars { get; set; } = 24000;
    public int MaxRecentStateTokens { get; set; } = 6000;
    public int MaxContextPackFiles { get; set; } = 12;
    public int MaxContextPackTotalChars { get; set; } = 80000;
    public int MaxContextPackFileChars { get; set; } = 20000;
    public int ChunkMaxChars { get; set; } = 2500;
    public int ChunkOverlapChars { get; set; } = 300;
    public int SemanticSearchTopK { get; set; } = 8;
    public string SessionRoot { get; set; } = ".agent/sessions";
    public string BackupRoot { get; set; } = ".agent/backups";
    public string IndexRoot { get; set; } = ".agent/index";
    public string PromptRoot { get; set; } = ".agent/prompts";
    public string[] AgentInstructionFiles { get; set; } = ["AGENT.md", "AGENTS.md", "AGENT.local.md"];
    public string[] BlockedFileNames { get; set; } = [".env", ".env.local", ".env.production", ".npmrc", ".pypirc", "id_rsa", "id_ed25519", "known_hosts"];
    public string[] BlockedExtensions { get; set; } = [".pem", ".key", ".p12", ".pfx"];
    public string[] AlwaysIgnoredPaths { get; set; } = [".git", ".vs", ".agent", "bin", "obj", "node_modules"];
    public string[] BlockedCommandTokens { get; set; } = ["&&", "||", ";", "|", ">", "<", "`", "$("];
    public string[] BlockedCommandPrefixes { get; set; } = ["rm ", "rmdir ", "del ", "git push", "sudo ", "chmod ", "chown ", "curl ", "wget ", "ssh ", "scp ", "env", "printenv"];
    public Dictionary<string, string> PromptOverrides { get; set; } = [];
    public LspConfig Lsp { get; set; } = new();

    public static AgentConfig Load(string repoRoot)
    {
        var path = Path.Combine(repoRoot, ".agent", "config.json");
        _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        AgentConfig config;
        if (!File.Exists(path))
        {
            config = new AgentConfig
            {
                ConfigPath = path
            };
            File.WriteAllText(path, JsonSerializer.Serialize(config, JsonOptions()));
            return config;
        }
        var raw = File.ReadAllText(path);
        config = JsonSerializer.Deserialize<AgentConfig>(raw, JsonOptions()) ?? new AgentConfig();
        config.Lsp ??= new LspConfig();
        EnsureDefaultLspServers(config.Lsp);
        config.ConfigPath = path;
        var normalized = JsonSerializer.Serialize(config, JsonOptions());
        if (!string.Equals(raw.Trim(), normalized.Trim(), StringComparison.Ordinal))
        {
            File.WriteAllText(path, normalized);
        }
        return config;
    }

    public async Task SaveAsync()
    {
        if (string.IsNullOrWhiteSpace(ConfigPath))
        {
            throw new InvalidOperationException("ConfigPath is not set.");
        }

        _ = Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        await File.WriteAllTextAsync(ConfigPath, JsonSerializer.Serialize(this, JsonOptions()));
    }

    public static JsonSerializerOptions JsonOptions() => new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static List<LspServerConfig> CreateDefaultLspServers() =>
    [
        new() { Id="go", Language="go", Command="gopls", FileExtensions=[".go"], RootMarkers=["go.mod"] },
        new() { Id="rust", Language="rust", Command="rust-analyzer", FileExtensions=[".rs"], RootMarkers=["Cargo.toml"] },
        new() { Id="typescript", Language="typescript", Command="typescript-language-server", Args=["--stdio"], FileExtensions=[".ts", ".tsx", ".js", ".jsx"], RootMarkers=["package.json", "tsconfig.json"] },
        new() { Id="csharp", Language="csharp", Command="csharp-ls", FileExtensions=[".cs", ".csx"], RootMarkers=["*.csproj", "*.sln", "Directory.Build.props"] }
    ];

    private static void EnsureDefaultLspServers(LspConfig lsp)
    {
        lsp.Servers ??= [];
        foreach (LspServerConfig server in CreateDefaultLspServers())
        {
            LspServerConfig? existing = lsp.Servers.FirstOrDefault(s => string.Equals(s.Id, server.Id, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                lsp.Servers.Add(server);
                continue;
            }

            if (string.IsNullOrWhiteSpace(existing.Language))
            {
                existing.Language = server.Language;
            }

            if (string.IsNullOrWhiteSpace(existing.Command))
            {
                existing.Command = server.Command;
            }

            if ((existing.Args?.Length ?? 0) == 0)
            {
                existing.Args = server.Args;
            }

            if ((existing.FileExtensions?.Length ?? 0) == 0)
            {
                existing.FileExtensions = server.FileExtensions;
            }

            if ((existing.RootMarkers?.Length ?? 0) == 0)
            {
                existing.RootMarkers = server.RootMarkers;
            }
        }
    }
}

public sealed class LspConfig
{
    public bool Enabled { get; set; } = true;
    public int StartupTimeoutSeconds { get; set; } = 10;
    public int RequestTimeoutSeconds { get; set; } = 20;
    public List<LspServerConfig> Servers
    {
        get; set;
    } =
        AgentConfig.CreateDefaultLspServers();
}

public sealed class LspServerConfig
{
    public string Id { get; set; } = "";
    public string Language { get; set; } = "";
    public string Command { get; set; } = "";
    public string[] Args { get; set; } = [];
    public string[] FileExtensions { get; set; } = [];
    public string[] RootMarkers { get; set; } = [];
    public bool Enabled { get; set; } = true;
}
