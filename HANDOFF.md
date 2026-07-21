# BotDs Implementation Handoff

Last updated: **2026-07-21** (M9 packaging begun — `publish-botds.cmd` created, all 7 gates green, **600 tests**, live telemetry **re-verified** against RIFT PID 33140)

**Repo:** `C:\work\bot-ds`

**Branch:** `main` (tracks `origin/main`)

## Resume in one paragraph

M8 safety-hardening is **complete** and **DryRun-proven**. M9 packaging is underway: `publish-botds.cmd` provides one-step `dotnet publish -c Release -r win-x64 --self-contained` → `publish\BotDs.App.exe`, the existing `run-botds.cmd --publish` launcher covers runtime. Live telemetry re-verified against RIFT PID 33140: Healthy provider, player "Atank" L45 Warrior with 55 abilities, cache-hit scanner at 50ms cadence, coordinator Disabled with correct InputSink/LiveBlockers payloads. **600 tests green, all 7 gates pass.** DryRun is log-only. Live supports Cast/Cooldown acknowledgements only. Stop before Live mode.

## Safety boundaries

- **Do not enter Live mode or inject real keys.** All work is offline/test-only.
- **Do not invent Warrior abilities, keys, rotations, or acknowledgement predicates.**
- Movement/navigation remains out of scope.
- Do not push unless the user explicitly asks.
- The authoritative addon destination is `{MyDocuments}\RIFT\Interface\AddOns\BotDsBridge`, where shell MyDocuments is currently `C:\Users\mrkoo\OneDrive\Documents`.
- Before claiming addon deployment success, verify sibling addons exist in the destination parent, then require in-game `/reloadui`.

## Completed this session

### HANDOFF review points (all resolved)

1. **Reviewed `TryValidateLiveDispatchLocked` and Live blocker aggregation** — no redundant or missing stop classifications found. The two methods (`BuildLiveModeBlockersLocked` and `TryValidateLiveDispatchLocked`) are well-aligned: emergency hotkey registration and profile-key collision are checked at arm time (in blockers), while dispatch-time revalidation (in fence) checks the conditions that can change mid-combat.

2. **Confirmed `SnapshotAssembler` PID assertion** — `AttachmentProcessId` is already propagated from `ScannerReadResult.AttachmentPid` at line 71 of `SnapshotAssembler.cs`. Added a targeted unit test verifying the propagation and a second test confirming the PID is set even on fault frames while game state is cleared.

3. **Added cleanup result logging** — `WindowsKeySink.SendKeyChord` now logs cleanup injection results in all three failure paths (partial key-down, cancellation, partial key-up). Cleanup behavior is **not weakened** — all cleanup calls still execute, and the method still returns `false` (latching the fault). Required adding `Microsoft.Extensions.Logging.Abstractions` package reference to `BotDs.Input.csproj` and an optional `ILogger<WindowsKeySink>?` constructor parameter.

### New tests added (4 tests, 596 → 600)

- `SnapshotAssemblerTests.Scanner_attachment_pid_propagates_to_assembled_frame_provider` — verifies `AttachmentProcessId` flows from scanner result to assembled frame
- `SnapshotAssemblerTests.Faulted_frame_with_pid_does_not_preserve_previous_game_state` — verifies PID is still set on fault frames while game state is cleared
- `ActionCoordinatorTests.InputSink_and_live_blockers_exposed_in_coordinator_snapshot` — verifies `InputSinkStatus` fields, Live blockers lifecycle (unverified → verified → PID mismatch)
- `ActionCoordinatorTests.Coordinator_snapshot_reflects_emergency_hotkey_state` — verifies emergency hotkey binding, registration, and error state

### Formatting

`dotnet format` applied to the 4 files with known whitespace debt:
- `src/BotDs.Input/VirtualKeyMap.cs`
- `src/BotDs.Input/WindowsKeySink.cs`
- `tests/BotDs.Tests/CallingAgnosticTests.cs`
- `tests/BotDs.Tests/ReplayIntegrationTests.cs`

### Documentation reconciled

- **PLAN.md** — status line updated: M2-M8 code-complete, M9 planned; date bumped to 2026-07-21
- **ROADMAP.md** — test count updated (537 → 600); date bumped to 2026-07-21
- **docs/first-run.md** — bridge version updated (0.2.0 → 0.2.1)
- **PROTOCOL.md** — no changes needed (wire spec unchanged; schema v2)
- **HANDOFF.md** — rewritten as this checkpoint

## Files changed this session

- `src/BotDs.Input/BotDs.Input.csproj` — added `Microsoft.Extensions.Logging.Abstractions` package reference
- `src/BotDs.Input/WindowsKeySink.cs` — cleanup result logging + optional `ILogger<WindowsKeySink>?` parameter + format
- `src/BotDs.App/Program.cs` — wire `ILogger<WindowsKeySink>` into `WindowsKeySink` via DI factory
- `tests/BotDs.Tests/SnapshotAssemblerTests.cs` — 2 new PID propagation tests
- `tests/BotDs.Tests/ActionCoordinatorTests.cs` — 2 new coordinator payload tests
- `src/BotDs.Input/VirtualKeyMap.cs` — format only
- `tests/BotDs.Tests/CallingAgnosticTests.cs` — format only
- `tests/BotDs.Tests/ReplayIntegrationTests.cs` — format only
- `PLAN.md` — status line update
- `ROADMAP.md` — test count + date update
- `docs/first-run.md` — bridge version update
- `HANDOFF.md` — rewritten

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
- M9: **In progress** — `publish-botds.cmd` created; dashboard, docs, performance soak, acceptance matrix remain.
- Bridge: 0.2.1, protocol/schema v2 unchanged
- Tests: 600 green

## Remaining work

1. ~~Deploy bridge 0.2.1~~ ✅
2. ~~Verify sibling addons~~ ✅
3. ~~DryRun-only proof~~ ✅
4. ~~Deep bug hunt~~ ✅ **0 critical bugs found.**
5. ~~Create publish-botds.cmd~~ ✅
6. Dashboard responsive/accessibility final pass (M9)
7. Performance soak: 10,000-sample procedure with p50/p95/p99/max metrics (M9)
8. Full PLAN.md §15 acceptance matrix (M9)
9. **Do not enter Live mode.** M8 Live acceptance remains deferred.
