# BotDs Implementation Handoff

Last updated: **2026-07-21** (M8 offline safety-hardening **complete**; 600 tests green; all gates passing)

**Repo:** `C:\work\bot-ds`

**Branch:** `main` (tracks `origin/main`)

## Resume in one paragraph

The M8 safety-hardening pass is **complete offline**. All three HANDOFF review points resolved, cleanup logging added to `WindowsKeySink`, four targeted tests added (SnapshotAssembler PID propagation + coordinator payload shape), `dotnet format` applied, all docs reconciled, and all 7 repository gates pass. **600 tests green.** Bridge is 0.2.1, protocol/schema v2. DryRun is log-only. Live supports Cast/Cooldown acknowledgements only. Live dispatch enforces sink capability/PID matching, verified bindings, emergency hotkey registration, pre-input fence revalidation, and Win32 partial-send cleanup with fault latching. Ready for bridge deploy + DryRun proof.

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
- M9: Planned
- Bridge: 0.2.1, protocol/schema v2 unchanged
- Tests: 600 green

## Remaining work (in order)

1. Deploy bridge 0.2.1 to `C:\Users\mrkoo\OneDrive\Documents\RIFT\Interface\AddOns\BotDsBridge\`
2. Verify sibling addons exist in parent directory
3. In-game `/reloadui` → confirm "BotDs Bridge v0.2.1" chat message
4. DryRun-only proof: Healthy schema-v2 telemetry, abilities known, action bar populated, decisions visible, `PendingAction=null`, bindings unchanged, no generated input with chat focused
5. Stop before Live mode. Do not claim M8 Live completion.
6. **Optional**: reconcile `knowledge.md` (still says 378 tests, M1 Active — low priority since HANDOFF/ROADMAP are authoritative)
