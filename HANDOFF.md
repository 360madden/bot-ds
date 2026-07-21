# BotDs Implementation Handoff

Last updated: **2026-07-21** (M9 packaging — `publish-botds.cmd` **verified self-contained**, BotDs.App.exe smoke-tested, all 7 gates green, **600 tests**)

**Repo:** `C:\work\bot-ds`

**Branch:** `main` (tracks `origin/main`)

## Resume in one paragraph

M8 safety-hardening is **complete** and **DryRun-proven**. M9 packaging is **verified**: `publish-botds.cmd` produces a self-contained 107 MB `publish\BotDs.App.exe` (361 files, coreclr.dll confirmed, .NET 10.0.8 runtime bundled). The published binary was smoke-tested — starts, binds port 5068 (via `ASPNETCORE_URLS`), serves HTTP 200, `/api/status` and `/api/coordinator` respond correctly. `run-botds.cmd --publish` covers runtime. The `--self-contained true` batch-line-continuation bug was fixed (`-p:SelfContained=true`). Live telemetry re-verified: RIFT PID 33140, Healthy, 55 abilities, cache-hit scanner. **600 tests green, all 7 gates pass.** DryRun is log-only. Stop before Live mode.

## Safety boundaries

- **Do not enter Live mode or inject real keys.** All work is offline/test-only.
- **Do not invent Warrior abilities, keys, rotations, or acknowledgement predicates.**
- Movement/navigation remains out of scope.
- Do not push unless the user explicitly asks.
- The authoritative addon destination is `{MyDocuments}\RIFT\Interface\AddOns\BotDsBridge`, where shell MyDocuments is currently `C:\Users\mrkoo\OneDrive\Documents`.
- Before claiming addon deployment success, verify sibling addons exist in the destination parent, then require in-game `/reloadui`.

## Completed this session (2026-07-21b — M9 publish verification)

### Publish verification

- **Created `publish-botds.cmd`** — one-step `dotnet publish -c Release -r win-x64 -p:SelfContained=true`
- **Fixed self-contained bug** — `--self-contained true` with `^` line continuations was silently dropped by batch parsing, producing a framework-dependent 1.4 MB publish instead of self-contained. Fixed to `-p:SelfContained=true` on a single line.
- **Smoke-tested published binary** — `BotDs.App.exe` starts, binds port 5068 (via `ASPNETCORE_URLS`), serves HTTP 200 dashboard, `/api/status` and `/api/coordinator` respond correctly
- **Publish stats**: 107 MB, 361 files, `coreclr.dll` present (self-contained confirmed), .NET 10.0.8 runtime bundled
- **Live telemetry re-verified**: RIFT PID 33140, Healthy provider, 55 abilities, cache-hit scanner at 50ms cadence
- **Launcher**: `run-botds.cmd --publish` already handles `ASPNETCORE_URLS` and runtime env
- **Docs updated**: ROADMAP.md M9→In Progress, first-run.md references `publish-botds.cmd`, HANDOFF checkpoint

### Gate status (all passing)

```text
dotnet build --no-restore  ✅ 0w 0e
dotnet test               ✅ 600 passed
dotnet format              ✅
node --check app.js        ✅
luac -p main.lua           ✅
git diff --check           ✅
```

## Current gate status (all passing)

```text
dotnet restore BotDs.sln          ✅
dotnet build BotDs.sln --no-restore  ✅ (0 warnings, 0 errors)
dotnet test BotDs.sln              ✅ (600 passed, 0 failed, 0 skipped)
dotnet format BotDs.sln --verify-no-changes --no-restore  ✅
node --check src/BotDs.App/wwwroot/js/app.js  ✅
luac -p addons/BotDsBridge/BotDsBridge/main.lua  ✅
git diff --check                   ✅
```

## Current milestone truth

- M0-M7: Complete
- M8: **Offline safety-hardening complete.** Live acceptance deferred.
- M9: **In progress** — publish script created + verified self-contained; dashboard polish, performance soak, acceptance matrix remain.
- Bridge: 0.2.1, protocol/schema v2 unchanged
- Tests: 600 green
- Publish: 107 MB self-contained at `publish\BotDs.App.exe` (gitignored)

## Remaining work

1. ~~Deploy bridge 0.2.1~~ ✅
2. ~~Verify sibling addons~~ ✅
3. ~~DryRun-only proof~~ ✅
4. ~~Deep bug hunt~~ ✅ **0 critical bugs found.**
5. ~~Create publish-botds.cmd~~ ✅
6. ~~Verify self-contained publish + smoke test~~ ✅
7. Dashboard responsive/accessibility final pass (M9)
8. Performance soak: 10,000-sample procedure with p50/p95/p99/max metrics (M9)
9. Full PLAN.md §15 acceptance matrix (M9)
10. **Do not enter Live mode.** M8 Live acceptance remains deferred.
