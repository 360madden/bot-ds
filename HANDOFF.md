# BotDs Implementation Handoff

Last updated: **2026-07-21** (early checkpoint; authoritative resume snapshot)

**Repo:** `C:\work\bot-ds`

**Branch:** `main` (tracks `origin/main`)

**Starting HEAD:** `615e1b89e4582bb3865a803ecb03a30db9f5d0a3`

**Checkpoint intent:** save the partially completed M8 safety-hardening pass. No push and no Live input were performed.

## Resume in one paragraph

The approved **BotDs M8 Safety-Hardening Plan** is partially implemented and offline-green. DryRun is now log-only and does not call `IKeySink`, create acknowledgements, mutate binding verification, or run external-action conflict detection. Live arming now requires a Live-capable ready sink whose positive PID matches telemetry, known-ready game input, verified bindings, a registered emergency hotkey, and only Cast/Cooldown acknowledgements. Live dispatch re-fetches and re-evaluates current telemetry behind a pre-input fence. Win32 input reports partial sends and performs reverse-order cleanup. Scanner partial ambiguity is fatal. Lua bridge version is **0.2.1**, schema remains **v2**, and ability/action-bar/input-readiness knownness was tightened. The implementation still needs a final code review, formatter pass, formal docs reconciliation, all required gates, addon deployment, and a DryRun-only live proof.

## Safety boundaries

- **Do not enter Live mode or inject real keys.** All work so far was offline/test-only.
- **Do not invent Warrior abilities, keys, rotations, or acknowledgement predicates.**
- Movement/navigation remains out of scope.
- Do not push unless the user explicitly asks.
- The authoritative addon destination is `{MyDocuments}\RIFT\Interface\AddOns\BotDsBridge`, where shell MyDocuments is currently `C:\Users\mrkoo\OneDrive\Documents`.
- Before claiming addon deployment success, verify sibling addons exist in the destination parent, then require in-game `/reloadui`.

## Implemented in this checkpoint

### DryRun and coordinator

- DryRun records `Dispatched` with `detail = "dry-run"` while keeping rate limits/history.
- DryRun never calls the sink, creates pending state, acknowledges actions, verifies bindings, or stops for external game actions.
- `ActionDecision` now carries optional provider session, source generation, target ID, and attachment PID.
- Live dispatch requires the latest usable frame, wrap-aware same/newer sequence, stable session/source/PID/target, known-ready input, live player/hostile target, and an available/ready/usable/in-range ability.
- The active profile is re-run through `CombatEvaluator`; the winning rule/alias/ability/key/ack must match exactly.
- Provider integrity/sink faults stop the controller and force output Disabled; transient game-state changes reject without input or pending state.

### Live sink and API gates

- `IKeySink.SupportsLiveInput` added (`FakeKeySink=false`, `WindowsKeySink=true`).
- Live arming rejects fake, unready, unbound, or PID-mismatched sinks.
- Coordinator/readiness/status payloads expose sink capability/readiness/bound PID and Live blockers.
- Live supports only ability-specific `Cast` and `Cooldown` acknowledgements. `Resource`, `Aura`, and `CombatEvent` remain load-compatible but cannot match or verify.

### Win32 input

- `IInputInjector.Inject` returns `InputInjectionResult(SentCount, NativeErrorCode)`.
- Partial key-down, cancellation, and partial key-up trigger one full reverse-order key-up cleanup batch.
- Failed original sends/cleanup paths latch the sink fault through dispatch failure.
- Dispatch rejects the target key or any physical Shift/Ctrl/Alt modifier being held.
- Existing pre/post foreground checks and bounded chord duration remain.

### Reader and bridge

- `ProviderStatus` carries optional attachment PID; `SnapshotAssembler` populates it from `ScannerReadResult.AttachmentPid`.
- Limit-capped scans recover only with a unique selection; `Ambiguous` maps to `CandidateAmbiguous` for initial and relocation scans.
- Lua input focus probing now emits separate known/ready flags; absent/erroring probes are unknown, never ready.
- Ability inventory is published known only when enumeration and every detail lookup complete below the cap.
- Action bar is published known only when page and all slot calls succeed.
- Bridge bumped to **0.2.1** in TOC and Lua; protocol/schema remains **v2**.

## Files changed

- `addons/BotDsBridge/BotDsBridge/RiftAddon.toc`
- `addons/BotDsBridge/BotDsBridge/main.lua`
- `src/BotDs.App/Endpoints/DashboardEndpoints.cs`
- `src/BotDs.App/Services/ActionCoordinator.cs`
- `src/BotDs.App/Services/ArmingReadinessService.cs`
- `src/BotDs.App/Services/SnapshotAssembler.cs`
- `src/BotDs.Core/ActionAcknowledgement.cs`
- `src/BotDs.Core/CombatEvaluator.cs`
- `src/BotDs.Core/StateModels.cs`
- `src/BotDs.Input/IKeySink.cs`
- `src/BotDs.Input/WindowsKeySink.cs`
- `src/BotDs.Reader/V5ScannerService.cs`
- coordinator/ack/scanner/Win32 tests under `tests/BotDs.Tests/`
- new `tests/BotDs.Tests/TestLiveKeySink.cs`

## Validation at checkpoint

Completed successfully after the latest edits:

```text
dotnet build BotDs.sln --no-restore
  Build succeeded; 0 warnings; 0 errors

dotnet test BotDs.sln --no-build
  596 passed; 0 failed; 0 skipped

luac -p addons/BotDsBridge/BotDsBridge/main.lua
  passed

git diff --check
  passed (line-ending conversion warnings only)
```

Earlier in the same pass, targeted `ActionCoordinatorTests` also passed **19/19**.

Not yet rerun after this safety slice:

- `dotnet restore BotDs.sln`
- `dotnet format BotDs.sln --verify-no-changes --no-restore`
- `node --check src/BotDs.App/wwwroot/js/app.js`
- final build/test after formatting and docs
- live DryRun proof

Known pre-existing formatter debt was 185 whitespace changes in:

- `src/BotDs.Input/VirtualKeyMap.cs`
- `src/BotDs.Input/WindowsKeySink.cs`
- `tests/BotDs.Tests/CallingAgnosticTests.cs`
- `tests/BotDs.Tests/ReplayIntegrationTests.cs`

The approved plan explicitly allows formatter-required changes in those files; keep formatter edits mechanical.

## Important review points before continuing

1. Review `ActionCoordinator.TryValidateLiveDispatchLocked` and the Live blocker aggregation for redundant/missing stop classifications.
2. Add/confirm an assertion that `SnapshotAssembler` exposes the scanner attachment PID.
3. Consider whether cleanup result details need logging beyond fault latching; do not weaken cleanup.
4. Confirm Lua action-bar and ability knownness with the actual client API during DryRun proof.
5. Recheck API JSON snapshots/tests for the additive sink and blocker fields.

## Required remaining work, in order

1. Inspect `git show --stat` for this checkpoint and continue from the committed tree.
2. Finish targeted tests for provider PID propagation/API payloads and any gaps found in review.
3. Run `dotnet format BotDs.sln` once; retain only formatter-required changes.
4. Reconcile `PLAN.md`, `ROADMAP.md`, `addons/BotDsBridge/PROTOCOL.md`, `docs/first-run.md`, and this handoff:
   - log-only DryRun
   - supported Live ack kinds
   - Live sink/PID/input gates
   - bridge 0.2.1, schema v2
   - M8 offline hardening complete, Live acceptance deferred
5. Run every required gate:
   - `dotnet restore BotDs.sln`
   - `dotnet build BotDs.sln --no-restore`
   - `dotnet test BotDs.sln --no-build`
   - `dotnet format BotDs.sln --verify-no-changes --no-restore`
   - `node --check src/BotDs.App/wwwroot/js/app.js`
   - `luac -p addons/BotDsBridge/BotDsBridge/main.lua`
   - `git diff --check`
6. Only after offline gates pass, deploy bridge 0.2.1 to the authoritative OneDrive AddOns path and verify sibling addons.
7. Require `/reloadui`, then perform **DryRun-only** proof: Healthy schema-v2 telemetry, honest list knownness, decisions visible, `PendingAction=null`, bindings unchanged, and no generated input with chat focused.
8. Stop before Live mode. Do not claim M8 Live completion.

## Current milestone truth

- M0-M7: previously complete.
- M8: **offline safety-hardening implementation in progress; offline tests green at checkpoint.**
- M8 Live input acceptance: **deferred and unproven.**
- Bridge: **0.2.1 semantics, protocol/schema v2 unchanged.**
