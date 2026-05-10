# Fetch

A local terminal/TUI coding-agent harness for Ollama/Qwen-style local LLMs.

## Requirements

- .NET 10 SDK
- `rg` / ripgrep recommended
- Ollama running locally
- Thinking model with tool support: `ollama pull qwen3.6:35b` 
- Embedding model: `ollama pull nomic-embed-text:latest`
- Optional LSP servers: `gopls`, `rust-analyzer`, `typescript-language-server`, `csharp-ls`, etc.

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
Load the thinking model
```bash
ollama pull qwen3.6:35b
curl http://localhost:11434/api/chat -d '{
  "model": "qwen3.6:35b",
  "messages": [
    { "role": "user", "content": "ping" }
  ],
  "stream": false,
  "think": true,
  "options": {
    "num_ctx": 100000,
    "temperature": 0
  }
}'
```
Tool calling is required. We pass a `tools` array to `/api/chat` and consume `message.tool_calls` plus `message.thinking` from the response.

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

Default command opens the TUI.

Fallback CLI:

```bash
dotnet run -- chat
dotnet run -- agent "fix failing tests"
```

The agent requires chat-based native tool calling with Qwen, reasoning preservation, and a 100k context window. Prompt-facing budgets expose token settings for recent state and tool results, while the older char limits remain as compatibility caps. Keep the larger tool/context limits if you want the agent to benefit from that wider context.

Useful slash commands in chat mode:

```text
/help          Show commands
/session       Show current session
/todos         Show todo list
/status        Run git status
/diff          Run git diff
/log           Show recent session log
/history       Show recent command history
/compact       Compact session log into summary.md
/health        Check Ollama, embeddings, rg, index, config, and LSP
/mode MODE     Set approval mode: read-only|ask|auto-safe|dry-run|yolo
/prompts       Export default prompts
/config        Show config path
/index         Build semantic index
/last-tool     Show last tool execution
/last-error    Show last failed tool execution
/last-command  Show last shell command
/replay        Replay last shell command
/clear         Clear terminal
/exit          Exit
```
