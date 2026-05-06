# Fetch Agent Guide

You are operating inside Fetch, a local .NET coding-agent harness with TUI, chat, and autonomous agent modes.

## Priorities

- Use tools instead of guessing.
- Keep edits small, local, and easy to verify.
- Read before editing.
- After code changes, run the narrowest relevant validation step.
- Prefer reporting a real blocker over pretending a tool succeeded.

## Normal Workflow

1. Read the task, current todos, and nearby code.
2. Use the smallest search/read tool that can identify the controlling code path.
3. Update todos so exactly one item is `in_progress`.
4. Make a focused patch.
5. Validate immediately with the cheapest relevant build, test, or command.
6. Only widen scope if the previous check changes the diagnosis.

## Tool Routing

- Use `symbol_search` for definitions.
- Use `references_search` for call sites and usages.
- Use `semantic_search` when you know the concept but not the exact symbol or file.
- Use `search_content` for exact text, errors, or identifiers.
- Use `read_ranges` for targeted code context.
- Use `context_pack` only when several files must be compared together.
- Use `apply_diff` for edits. If it fails, reread the exact file/range and retry with a smaller patch.
- Use `run_command` only for a concrete verification or repo task.

## Editing Rules

- Preserve existing style and public APIs unless the task requires change.
- Do not make speculative refactors.
- Do not claim a file was created, patched, or deleted unless the tool result confirms it.
- Treat tool failures, denied approvals, stale patches, and missing files as real blockers to resolve.
- Prefer repo-root relative paths that stay inside the sandbox.

## Validation

- For fixes: run -> analyze -> patch -> rerun.
- Prefer the narrowest validation that can fail your hypothesis.
- If a build or test fails, use the result to drive the next local change.
- After meaningful edits, inspect the resulting diff and summarize the actual outcome.

## Fetch-Specific Constraints

- Approval mode may block mutations or commands. Respect it and wait for approval when required.
- Shell policy blocks risky operators such as pipes, redirects, command chaining, and privileged commands.
- Sensitive files and paths such as `.env`, keys, `.git`, and `.agent` are restricted.
- LSP tools may be unavailable; fall back to text or semantic search when needed.
- Tool outputs can be plain text or JSON and may be truncated. Read them carefully.

## Repo Hints

- `Program.cs` wires the runtime modes and tool list.
- `Core/AgentLoop.cs` controls planning, tool execution, approvals, and final responses.
- `Prompts/PromptCatalog.cs` defines the base planner/router/agent prompt rules.
- `Approval/ApprovalPolicy.cs` and `Config/AgentConfig.cs` define approval and safety behavior.
- `Tools/` contains the concrete tool semantics.

## Response Expectations

- Be explicit about what you know, what changed, and what was verified.
- If a command or tool result contradicts the plan, trust the result and adapt.
- Finish with the smallest accurate summary of the work completed and any remaining blocker.