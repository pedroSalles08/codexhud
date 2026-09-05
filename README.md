# Codex HUD

A compact, always-on-top companion HUD for observing Codex Desktop usage.

## Download and install

1. Open the [latest release](https://github.com/pedroSalles08/codexhud/releases/latest).
2. Download `CodexHUD-Setup-x64.exe`.
3. Run the installer.

The installer is self-contained, so the .NET runtime does not have to be installed
separately. It adds CodexHUD to the Start menu, which also makes it discoverable by
searching for `CodexHUD` in Windows. Uninstall it later from **Settings > Apps >
Installed apps**.

The installer is not code-signed yet, so Windows may identify the publisher as
unknown until the project adopts a signing certificate.

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
- presents the three primary readings in a compact WPF instrument;
- expands in place for reset times, freshness, context tokens, and additional buckets;
- uses a restrained Windows backdrop when supported, with an opaque fallback.

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

## Build the Windows installer

Install the .NET 10 SDK and Inno Setup 6 or 7, then run:

```powershell
.\scripts\build-installer.ps1
```

The self-contained app and installer are written under `artifacts/`.

## Publish a release

Pushing a version tag such as `v0.1.0` runs the test suite, builds the x64 installer,
and publishes it on GitHub Releases automatically.

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
