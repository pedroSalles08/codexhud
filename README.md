# Codex HUD

Technical vertical slice for observing Codex Desktop usage from rollout JSONL files.
This is not the final overlay UI.

## Current scope

- locates `CODEX_HOME` (or the user's `.codex` directory) and its `sessions` tree;
- accepts only root sessions whose metadata says `originator = Codex Desktop` and
  `source = vscode`;
- incrementally tails complete JSONL lines with a per-file byte offset;
- extracts only `session_meta` identity and `event_msg/token_count` usage metadata;
- exposes last-turn context usage, context-window size, and rate-limit snapshots;
- reconciles rate limits with one short-lived, read-only Codex App Server probe at
  startup and after a displayed post-reset estimate;
- watches for filesystem changes without a periodic polling loop;
- marks a rate-limit window as `~100%` after its `resets_at` passes, until Codex
  writes a fresh authoritative snapshot;
- presents the state in a deliberately temporary WPF diagnostic window.

The monitor is read-only. The probe uses only `initialize`, the `initialized`
notification, and `account/rateLimits/read`; it never invokes a thread method. It
does not access `auth.json`, change Codex state, or log rollout records, prompts,
responses, tool arguments, credentials, or complete App Server responses.

Context remaining follows the behavior verified against the current Codex Desktop
`/status`: `(model_context_window - last_token_usage.total_tokens) /
model_context_window`, rounded to the nearest whole percentage.

## Run

```powershell
dotnet run --project src/CodexHud.App/CodexHud.App.csproj
```

## Test

```powershell
dotnet test CodexHud.sln --filter "TestCategory!=LocalIntegration"
```

The opt-in local smoke test reads the current machine's rollouts and prints only
sanitized usage metadata:

```powershell
$env:CODEX_HUD_REAL_SMOKE = '1'
dotnet test tests/CodexHud.Core.Tests/CodexHud.Core.Tests.csproj `
  --filter "TestCategory=LocalIntegration" `
  --logger "console;verbosity=detailed"
```
