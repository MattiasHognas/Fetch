# Fetch

A local terminal/TUI coding-agent harness for Ollama/Qwen-style local LLMs.

## Requirements

- .NET 10 SDK
- Ollama running locally
- `rg` / ripgrep recommended
- Optional embedding model: `ollama pull nomic-embed-text`
- Optional LSP servers: `gopls`, `rust-analyzer`, `typescript-language-server`, etc.

### Install Ollama
```bash
sudo pacman -Syu
sudo pacman -S ollama
sudo pacman -S ollama-cuda
sudo systemctl restart ollama
sudo systemctl enable --now ollama
ollama --version
```

### Run Ollama
Load coder model
```bash
ollama pull deepseek-coder-v2:16b
curl http://localhost:11434/api/generate -d '{
  "model": "deepseek-coder-v2:16b",
  "prompt": "ping",
  "stream": false,
  "keep_alive": -1
}'
```
Load embedding model
```bash
ollama pull nomic-embed-text:latest
curl http://localhost:11434/api/embeddings -d '{
  "model": "nomic-embed-text",
  "prompt": "ping",
  "keep_alive": -1
}'
````

## Run

```bash
dotnet restore
dotnet run
```

Default command opens the Terminal.Gui TUI.

Fallback CLI:

```bash
dotnet run -- chat
dotnet run -- agent "fix failing tests"
```

Useful slash commands in chat mode:

```text
/help
/health
/mode ask|read-only|auto-safe|dry-run|yolo
/index
/last-tool
/last-error
/last-command
/replay
/prompts
```
