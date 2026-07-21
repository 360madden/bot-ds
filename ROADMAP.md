# BotDs Formal Roadmap

Status: Active

Last updated: 2026-07-20

The architectural contract is defined in `PLAN.md`. This roadmap orders implementation from the current tested foundation to a fully working combat bot.

## Status Legend

| Status | Meaning |
| --- | --- |
| Complete | Implemented, verified, and committed |
| Active | Current highest-priority milestone |
| Planned | Defined but blocked by earlier dependencies |
| Decision gate | Requires measured evidence or user input before proceeding |

## Dependency Chain

```text
M0 Foundation [Complete]
  -> M1 Transport and current-client conformance [Decided]
      -> M2 Live telemetry provider [Code-complete — live soak deferred]
          -> M3 Hosted source, snapshot assembly, and replay [Complete]
              -> M4 Dashboard settings and observability [Complete]
              -> M5 Profiles and progression readiness [Complete]
              -> M6 Dry-run action coordinator [Complete]
                  -> M7 Foreground Windows input [Complete]
                      -> M8 Closed-loop live combat [Code-complete — live residual deferred]
                          -> M9 Final acceptance and packaging [Planned]
```

M4 dashboard work, M5 profile work, and portions of M6 dry-run work may proceed in parallel after M3 freezes the observation contract. Native input does not begin before M6 passes.

## M0: Tested Foundation

Status: Complete

### Delivered

- .NET 10 Core, Reader, App, and xUnit projects.
- Transport-neutral telemetry and combat profile models.
- Deterministic ordered combat evaluator.
- Versioned JSON profile schema and semantic validation.
- V5 binary protocol, CRC, bounded parser, continuity, and freshness tracking.
- Windows x64 process selection, attachment, readable-region scan, candidate relocation, stale backoff, and scanner metrics.
- Local dashboard host, authentication, SSE, controller lifecycle, profile reload, and structured logs.
- Provider-only Lua envelope and heartbeat prototype.
- 378 passing tests and component documentation.

### Remaining limitation

The repository cannot yet observe live RIFT state end to end or send game input.

## M1: Transport And Current-Client Conformance

Status: Decided

Goal: Select one reliable, low-latency addon-to-C# transport using measured current-client evidence.

### Decision

**V5 process memory selected** (2026-07-20). The 360madden/Reader tool reference confirmed the approach is viable: same sentinel-based scanning with ~95% cache hit rate. Our V5 scanner implements 3-tier scanning (cache hit → small-window rescan ±2MB → full scan). The conformance probe addon (`BotDsConformance`) validates live jit/ffi/GC behavior. SavedVariables are available for debug/config scaffolding.

### Work

1. Build a non-actuating addon conformance mode.
2. Produce a field/event matrix recording type, units, nil/false behavior, update trigger, knownness, cadence, secure-mode behavior, and fallback for:
   - player and `player.target` identity, availability, calling/capability identity, classification, relation, health, death, resources, combat state, and target loss;
   - cast start/end/channel/interruptibility;
   - ability list/detail, cooldown versus shared GCD, usability, range, costs, target, and add/remove events;
   - buff list/detail and add/change/remove events;
   - combat events needed by typed acknowledgements;
   - privacy-safe game-input readiness, including chat/edit focus, modal UI, key-binding screens, and loading;
   - frame time, client version, secure mode, zoning, loading, and reload behavior.
3. Measure observed resource kinds, ability/aura counts, identifier lengths, representative and worst-case payloads, inspection cost, event loss, resynchronization needs, and update cadence.
4. Prove which typed acknowledgement predicates are reliable for instant and cast-time abilities and document manual-input conflict behavior.
5. Confirm whether action-bar mappings are observable. If not, freeze the controlled key-calibration and no-binding-changes-while-armed contract.
6. Decide full-snapshot versus event-maintained cache behavior, per-section cadence, resynchronization interval, evidence age, complete-list semantics, and production protocol capacities.
7. Test for a real stable mutable addon storage primitive.
8. If stable storage passes `PLAN.md` section 4, retain V5 process memory.
9. Otherwise validate ROI-only addon-rendered optical telemetry, exact HWND/PID binding, and the existing ChromaLink approach.
10. Select one transport and update `PLAN.md`, `ROADMAP.md`, `README.md`, and `HANDOFF.md` to the frozen production contract before M2.

### Exit criteria

- Every required field is confirmed, rejected, or represented as explicitly unknown.
- Known-empty and incomplete lists are distinguishable.
- `KnownNoTarget` is distinct from target inspection failure.
- Calling or its capability-based replacement, death, resources, cooldown/GCD, range/usability, and supported acknowledgement semantics are resolved.
- Game-input readiness and observable/unobservable key-binding behavior are resolved.
- Exactly one production transport is selected.
- Production state/protocol layout, capacities, per-section cadence, resynchronization, and evidence-age rules are frozen from measured data.
- Source publication and external reading meet the latency target.
- Corruption, capture loss, address loss, secure-mode changes, zoning, and reload fail closed.
- A 30-minute 20 Hz read-only soak produces at least 36,000 expected publications, accepts at least 99.9 percent of valid publications, and rejects all 100 injected corrupt/malformed publications.
- No action output exists.

## M2: Live Telemetry Provider

Status: **Code-complete** (2026-07-21) — live end-to-end **proven** (Healthy + player/target/abilities); long soak / formal §15.1 residual still deferred

Goal: Replace the provider-only skeleton with complete live player and selected-target telemetry.

### Code complete (shipped + tested)

- Transport-neutral `ITelemetrySource` (`SnapshotTelemetrySource` over `SnapshotPublisher`); App DI registers it; no combat/profile/Warrior data in Reader or addon.
- V5 publication path: BotDsBridge emits ProviderInfo, Player, Target (including **KnownNoTarget** via present section + unavailable unit), Abilities, Player/Target auras; header flags for **GameInputReady** / **GameInputReadyKnown** (bits 2–3).
- Mapper: section-mask list knownness (known-empty vs unknown); `TargetKnownness` (`Unknown` / `KnownNoTarget` / `KnownTarget`); `GameInputReady` tri-state; fail-closed unavailable units.
- Hosted `TelemetryReaderLoop` + `SnapshotAssembler` publish normalized `TelemetryFrame` for dashboard/evaluator.
- Dashboard `/api/status` exposes `knownness` (target, abilities/auras, gameInputReady).
- Unit coverage: `M2LiveTelemetryTests` encode→parse→map for healthy multi-section, known-empty lists, no-target vs unknown, GameInputReady true/false/unknown, calling-agnostic source structure, DI source path. Suite green (**551** as of 2026-07-21f).
- Live (2026-07-21f): `rift_x64` attached, provider **Healthy**, player Atank L45 warrior, target present, **55 abilities**, knownness complete. Bridge 0.1.2 fixes Version table + sequence/materialize alignment.
- Bridge **0.2.0** / schema **v2** (2026-07-21h): ability **name** + seconds→ms CD/usable fidelity; action-bar observation; `/api/abilities` + `/api/action-bar`.

### Explicitly deferred live-only exit items

1. ~~Standalone read-only live probe reporting correct in-world state end-to-end.~~ **Observed Healthy 2026-07-21f**
2. Live target select/loss within freshness budget (manual exercise still needed).
3. 30-minute provider soak with no unhandled Lua/Reader errors.
4. Full `PLAN.md` §15.1 live transitions (zoning, reload, secure mode, etc.).

**Do not mark M2 fully Complete until deferred live items pass.** Do not invent Warrior ability data.

### Work (historical checklist)

- ~~ITelemetrySource~~ · ~~V5 capture path~~ · ~~Player/target/abilities/auras sections~~ · ~~session/sequence/secure~~ · ~~list knownness~~ · ~~GameInputReady~~ · ~~calling-agnostic provider~~

## M3: Hosted Source, Snapshot Assembly, And Replay

Status: Complete

Goal: Connect live telemetry to `BotDs.App` without enabling input.

### Work

- Add a hosted telemetry source loop.
- Stamp each read/capture cycle with host-monotonic start/completion times and implement the short dispatch-fence contract used by M6.
- Replace fixed polling with latest-only source signaling where practical.
- Add source generation and structured failure mapping.
- Add heartbeat-aware full-snapshot assembly.
- Track transport liveness separately from per-section game-state evidence age; heartbeat receipt never refreshes action evidence.
- Clear carried state on fault, incomplete inspection, session change, reattach, or process exit.
- Publish normalized snapshots to `SnapshotPublisher`.
- Add read-only automatic source reconnection with no automatic arming.
- Add a bounded, versioned replay envelope covering observations, monotonic time, commands, generations, focus/process events, source faults, and dispatch outcomes. Optical recording is ROI data only.
- Expose source status and metrics through authenticated endpoints.
- Replace the 5000 ms application freshness default with the validated 100-500 ms range and reject out-of-range startup/settings values.
- Bind Kestrel explicitly to loopback and reject wildcard/non-loopback overrides.
- Move structured logging behind a bounded asynchronous channel with dropped-log and queue-utilization metrics.

### Exit criteria

- `/api/status` and SSE show correct live player and target state.
- Heartbeats never carry full state across a source/session boundary.
- Heartbeats never refresh the evidence age of carried player, target, ability, or aura state.
- Source failure becomes unusable before the configured freshness deadline.
- Restart, addon reload, zoning, and process exit recover observation without auto-arming.
- Replaying the same fixture produces identical normalized snapshots and evaluator results.
- Corrupted or oversized replay files, full-screen optical data, and unbounded recordings are rejected.
- The application still cannot send game input.

## M4: Dashboard Foundation, Settings, And Source Metrics

Status: Complete

Goal: Deliver the interactive dashboard foundation for contracts completed through M3. Later milestones add their own profile, action, and input vertical slices.

### Delivered

- Overview, telemetry, settings, and source/pipeline metrics views.
- Atomically persisted local settings service (LocalSettingsService).
- Settings forms for scanner interval/max-age/process, evaluator telemetry age/interval, dashboard interval/log-limit, log retention.
- Disarmed-state gate for combat-affecting settings changes.
- Source, pipeline, evaluator, controller, and scanner metrics in /api/status SSE stream.
- Dashboard SSE and polling independent of combat path.
- Responsive layout with desktop/tablet/phone breakpoints.
- Loopback-bound Kestrel with DashboardSecurityMiddleware.
- Host integration tests for settings, security, and endpoints.

### Remaining

- Percentile latency summaries (M8).
- Final responsive/accessibility pass (M9).

## M5: Profiles And Progression Readiness

Status: Complete

Goal: Make profiles safe, expressive, and reusable across characters and levels.

### Delivered

- Strict key-binding grammar and schema validation (KeyBindingValidator).
- Rule telemetry dependency and acknowledgement validation at reload.
- Centralized ArmingReadinessService shared across API, dashboard, evaluator, and coordinator.
- C# runtime validation authoritative with schema/runtime conformance tests.
- Dashboard profile editor: validate-before-commit, atomic temp-file write, corrupted-file recovery, config lease gating.
- Profile/readiness dashboard views with profile select, detail, and editor.
- ProgressionEdgeCaseTests: level boundaries, required/optional abilities, missing inventories, no-executable-rule cases.
- Required ability sets as build capability identity.
- Synthetic non-Warrior profile/replay fixtures for engine generality.

### Remaining

- Replace disabled Warrior fixture with real profile (requires user-supplied keys/rules and live telemetry — M8).
- Capture actual Warrior ability/aura IDs through live telemetry (requires M2/M8).

## M6: Dry-Run Action Coordinator

Status: Complete

Goal: Complete the full combat control loop without native input.

### Delivered

- Serialized ActionCoordinator with Disabled, DryRun, and Live output modes.
- Telemetry dispatch fence with `finally` release for all paths.
- Pre-dispatch revalidation: controller generation, state, pending-action timeout, global/per-key rate limits.
- One-pending-action semantics with bounded acknowledgement timeout.
- Pending action invalidation on output disable/disarm.
- Bounded DispatchRecord history (200 entries).
- FakeKeySink integration for dry-run dispatch recording.
- ReplayIntegrationTests: deterministic replay with FakeTimeProvider, combat cycle verification.
- ActionCoordinatorTests: rate limits, pending-action blocking, timeout, cancellation paths.
- Output mode control via dashboard API (SetOutputMode endpoint).

### Remaining

- Action-history and action-metric dashboard views as vertical slice (in progress).
- Live-mode typed acknowledgement verification (M8 — requires live game).
- Acknowledgement-based action tracking (M8).

## M7: Foreground Windows Input

Status: Complete

Goal: Add the narrow native boundary that sends configured combat keys.

### Delivered

- BotDs.Input project with IKeySink, IInputInjector, IForegroundProvider interfaces.
- WindowsKeySink: SendInput-based key chord dispatch with foreground validation.
- Key-down → delay (_chordPressMs) → key-up with correct modifier ordering.
- Immediate pre-dispatch foreground PID check via GetForegroundWindow/GetWindowThreadProcessId.
- Held-key rejection: target key, modifier keys, and binding's own modifiers (sticky modifier prevention).
- Post-dispatch foreground recheck with fault latch on change.
- Cancellation-safe cleanup: releases keys even after token cancel.
- VkMapper: 80+ virtual key codes (digits, letters, F1-F12, numpad, arrows, OEM symbols).
- FakeKeySink for testing with thread-safe history and fault support.
- IForegroundProvider/IInputInjector abstractions for testability.
- WindowsKeySinkTests: 17 tests (foreground, held-key, modifier, fault, lifecycle, injector).
- Feature-flagged via BotDs:Input:UseWindowsKeySink + BotDs:Input:BoundPid in Program.cs.

### Remaining

- Global emergency-stop hotkey registration (M8 — requires live game safety).
- Dedicated test-window executable for input testing (M9).
- Input-metric dashboard view (M8/M9).

## M8: Closed-Loop Live Combat

Status: **Code-complete** (2026-07-21) — live exit criteria deferred

Goal: Enable live output incrementally and prove observed acknowledgement.

### Code complete (shipped + tested)

- Control-authorized Live/DryRun/Disabled mode transitions; startup always coerces output to Disabled.
- Typed acknowledgement matcher (Cast/Cooldown/Resource/Aura) with baseline capture; only newer-sequence same-session evidence acknowledges; unrelated ability CD does not ack the pending ability.
- Pending observation every evaluator tick; Live ack-timeout latches `ActionNotAcknowledged` and disables output; one pending action blocks further combat keys.
- Binding verification (`Unverified`/`Verified`/`Mismatch`); Live requires verified required bindings.
- Global emergency hotkey (`Ctrl+Shift+F12` default) via `RegisterHotKey`; Live requires registration; press latches Stopped + Disabled.
- Profile keys that collide with the emergency hotkey are rejected for DryRun/Live mode changes.
- Target/session/source-generation change invalidates pending work; unexplained profile-ability cooldown while armed stops with `ExternalActionConflict`.
- Dashboard binding endpoints and coordinator snapshot (including hotkey registration).
- Closed-loop unit/integration coverage in `M8ClosedLoopGateTests` and related coordinator/ack/external-conflict tests (537 suite green as of 2026-07-21).

### Explicitly deferred live-only exit items

These require a loaded BotDsBridge publishing Healthy V5 frames, real Warrior ability IDs/keys/rules (user-supplied or observed — **not invented**), and interactive game scenarios. They are **not** claimed Complete:

1. In-game `/reloadui` → Healthy provider frames (scanner may attach with 0 candidates until addon emits).
2. Real Warrior dry-run decision comparison against expected actions.
3. Live key calibration soak and binding verification against observed ability transitions.
4. Full forced-failure matrix (death, focus loss, alt-tab, target switch, reload, etc.) in the live client.
5. 30-minute combat soak with ≥200 acknowledged dispatches and `PLAN.md` §15.1 live scenarios.
6. Combat-event acknowledgement section (no combat-event telemetry section yet).
7. Profile data tuning only after measured live timings — no Warrior engine branches.

### Work item map

1. ~~Live mode + readiness + startup Disabled~~
2. ~~Disarmed mode transitions + dashboard control~~
3. Real Warrior dry-run comparison — **deferred live**
4. Live calibration — **deferred live** (auto-Verified on successful ack exists in code)
5. ~~Binding verification required for Live~~
6. Typed ack path — **code complete**; live proof deferred
7–8. Multi-ability live enablement — **deferred live**
9. Combat-event ack — **deferred** (protocol gap)
10. ~~External-action conflict stop~~
11. Live failure matrix + estop soak — **deferred live** (hotkey code complete)
12. Profile-only tuning — **deferred live**

### Exit criteria (live residual)

Code-level fail-closed criteria are covered by tests. Remaining roadmap exit criteria that need Healthy live telemetry and real profile data stay open until deferred items 1–5 above are executed. **Do not mark M8 fully Complete until those live proofs exist.**

## M9: Final Acceptance And Packaging

Status: Planned

Goal: Deliver a repeatable, documented, fully working local combat bot.

### Work

- Complete the dashboard's final responsive desktop/mobile pass.
- Run browser interaction, accessibility, SSE reconnect, token-role, settings, profile editing, metric rendering, and 360x800/768x1024/1440x900 viewport tests.
- Add installation, addon setup, first-run configuration, profile authoring, metrics interpretation, and recovery documentation.
- Add local win-x64 publish configuration and a thin convenience launcher if useful.
- Add final recorded replay fixtures and full host integration tests.
- Run the documented 10,000-sample performance procedure and record p50/p95/p99/max, availability, dropped frames, CPU, working set, allocations, and GC counts.
- Run final findings-only architecture, protocol, security, concurrency, and regression reviews.
- Execute the complete acceptance matrix from `PLAN.md` section 15.

### Exit criteria

- Clean checkout setup succeeds using documented steps.
- Addon, application, dashboard, settings, metrics, profile, observation, evaluation, input, acknowledgement, and emergency stop work together.
- All repository verification commands pass.
- All live acceptance scenarios pass.
- The final 30-minute soak meets latency and correctness budgets.
- Every metric category required by `PLAN.md` is visible, bounded, and updates at its documented cadence.
- The accepted `SendInput` focus-race limitation is documented with measured focus-race tests and latching behavior.
- No known critical or high-severity defects remain.

## Parallel Work Strategy

After M1 selects the transport:

- Protocol/provider work owns `addons/**` and the selected `src/BotDs.Reader/**` transport.
- Core/profile work owns `src/BotDs.Core/**`, `profiles/**`, and `schemas/**`.
- Dashboard work owns `src/BotDs.App/wwwroot/**`.
- App orchestration work owns hosted services and settings, excluding frontend assets.
- Test work owns fixtures and tests but does not redefine production contracts independently.
- One integrator owns `BotDs.sln`, project files, `Program.cs`, shared contracts, and final DI wiring.
- A findings-only reviewer closes every milestone before the next dependent milestone starts.

Do not build complete memory and optical production transports in parallel. Do not enable native input before the dry-run action coordinator and replay gates pass.

## User Decision Checkpoints

| Checkpoint | Decision/input |
| --- | --- |
| End of M1 | Accept measured production transport and supported runtime constraints |
| During M4 | Select preferred defaults for cadence, freshness, and metrics display |
| During M5 | Supply Warrior keys, intended priority rules, and required aura behavior |
| End of M6 | Approve transition from disabled to dry-run operation |
| End of M7 | Accept measured `SendInput` focus-race behavior and select the emergency hotkey |
| Start of M8 | Approve transition from dry-run to incremental live key output |
| End of M9 | Accept final live soak and completed combat-only scope |
