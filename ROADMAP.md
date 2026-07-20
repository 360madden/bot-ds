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
  -> M1 Transport and current-client conformance [Active decision gate]
      -> M2 Live telemetry provider
          -> M3 Hosted source, snapshot assembly, and replay
              -> M4 Dashboard settings and observability
              -> M5 Profiles and progression readiness
              -> M6 Dry-run action coordinator
                  -> M7 Foreground Windows input
                      -> M8 Closed-loop live combat
                          -> M9 Final acceptance and packaging
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

Status: Active decision gate

Goal: Select one reliable, low-latency addon-to-C# transport using measured current-client evidence.

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

Status: Planned

Goal: Replace the provider-only skeleton with complete live player and selected-target telemetry.

### Work

- Introduce a small transport-neutral `ITelemetrySource` contract.
- Implement the selected publication/capture path only.
- Populate player identity, capability/calling, level, health, a resource map, combat state, liveness, and cast.
- Populate selected target identity, relation, NPC/player classification, health, combat state, and cast.
- Populate complete ability inventory with cooldown, usability, range, costs, cast time, passive, and channel state where available.
- Populate complete player and target aura inventories.
- Include client version, secure mode, session, sequence, and completeness.
- Include per-section evidence sequence/time and explicit player/target/list knownness.
- Publish privacy-safe `GameInputReady` knownness for chat/edit focus, modal UI, key-binding screens, loading, and other M1-proven blocked contexts.
- Implement the production protocol version frozen by M1; do not alter V5 silently.
- Keep all character and rotation data out of the addon and Reader.

### Exit criteria

- A standalone read-only probe reports correct live state.
- Target selection and loss appear within the freshness budget.
- Known no-target, target unknown, and a known target are distinct.
- Zero-count lists are known-empty; partial lists are unknown.
- Overflow and record caps publish no misleading state.
- Provider code contains no Warrior-specific IDs, levels, keys, or rules.
- A 30-minute provider soak has no unhandled Lua or Reader errors.
- The conformance workload in `PLAN.md` section 15.1 passes, including target, health/resource, cooldown/range, cast, aura, zoning, and reload transitions.

## M3: Hosted Source, Snapshot Assembly, And Replay

Status: Planned

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

Status: Planned

Goal: Deliver the interactive dashboard foundation for contracts completed through M3. Later milestones add their own profile, action, and input vertical slices.

### Work

- Add overview, telemetry, settings, and source/pipeline metrics views.
- Add an atomically persisted local settings service.
- Add validated settings forms for process/window selection, source cadence, freshness, dashboard cadence, replay limits, and log retention.
- Require disarmed state for combat-affecting settings changes.
- Indicate immediate versus restart-required changes.
- Require explicit control authorization metadata for every settings mutation.
- Add source, pipeline, evaluator, controller, and dashboard metrics currently implemented from `PLAN.md` section 13.
- Add bounded recent histories and percentile latency summaries.
- Keep dashboard/SSE work off the combat path and throttle it independently.
- Add endpoint-level host integration tests.
- Implement the responsive/accessibility requirements in `PLAN.md` section 11.4 for these views.

### Exit criteria

- All M3-supported settings can be viewed and changed through the dashboard.
- Invalid updates preserve the previous settings snapshot.
- Settings changes cannot race an armed evaluation.
- Dashboard displays source, telemetry, evaluator, and controller readiness/failure reasons available through M4.
- Every source, pipeline, evaluator, controller, and dashboard metric implemented through M4 is rendered and updates within the dashboard cadence budget.
- Slow or disconnected dashboard clients do not alter telemetry or evaluation latency.
- Loopback and token tests cover every endpoint.

## M5: Profiles And Progression Readiness

Status: Planned

Goal: Make profiles safe, expressive, and reusable across characters and levels.

### Work

- Add strict key-binding grammar and schema validation.
- Validate every rule's telemetry dependencies and acknowledgement support at reload.
- Centralize arming readiness so API, dashboard, evaluator, and action coordinator share one result.
- Change arming readiness to permit an armed waiting state without a target while still requiring healthy complete player/source state.
- Make C# runtime validation authoritative and add schema/runtime conformance tests.
- Add a control-authorized profile editor with validate-before-commit, atomic file replacement, corrupted-file recovery, and configuration leases.
- Add profile/readiness dashboard views as a vertical slice.
- Test level boundaries, required/optional abilities, missing inventories, aura dependencies, and no-executable-rule cases.
- Use required ability sets as build capability identity until a trustworthy build field exists.
- Capture actual Warrior ability and aura IDs through live telemetry.
- Replace the disabled fixture with a real profile only after the user supplies keys and intended rules.
- Add a synthetic non-Warrior profile/replay to enforce engine generality.

### Exit criteria

- Enabled profiles cannot contain unsupported keys, conditions, acknowledgements, or unobservable requirements.
- Profile writes are atomic, control-authorized, and cannot alter an armed runtime.
- Progression tests cover lower level, exact boundary, newly learned ability, removed ability, and changed build capability.
- A valid generic profile reaches dry-run decisions from replay.
- The real Warrior profile passes live readiness and remains data only.
- No calling-specific logic exists in `src/**`.

## M6: Dry-Run Action Coordinator

Status: Planned

Goal: Complete the full combat control loop without native input.

### Work

- Add one serialized `ActionCoordinator`.
- Add disabled and dry-run output modes, defaulting to disabled.
- Acquire the telemetry fence, drain/apply the newest observation, then revalidate controller, profile, settings, source, session, target, freshness, `GameInputReady`, ability, rule, and rate limits before a dry-run dispatch.
- Capture separate controller, profile, runtime-settings, source, process, window, session, and target identities in every action intent.
- Permit exactly one pending action.
- Add the default and hard-bounded global/per-key rate limits from `PLAN.md` section 9 with burst capacity one.
- Add only the typed acknowledgement predicates proven in M1: fence/drain/apply, revalidate, capture baseline, dispatch, install the watcher, then release and accept only a later cycle.
- Release the telemetry fence through a bounded `finally` path for success, rejection, failure, emergency stop, cancellation, and shutdown.
- Add bounded acknowledgement timeout with no blind retry.
- Invalidate pending actions on target, source, profile, controller, focus, or process changes.
- Add output-mode, rate-limit, acknowledgement-timeout, combat, action-history, and action-metric dashboard/settings views as a vertical slice.

### Exit criteria

- Repeated identical frames produce one dry-run action until acknowledgement.
- Unrelated state changes cannot acknowledge an action.
- A pre-dispatch higher-sequence snapshot cannot acknowledge an action.
- A drained invalidating observation prevents dispatch, and the first immediate post-fence observation can acknowledge without being lost.
- Stale, unknown, failed target precondition, changed-profile, and rate-limited scenarios emit no action.
- More than 10 global attempts or 4 attempts for one key in a one-second test window are blocked, and default limits block above 4/2 respectively.
- Timeout produces a specific latched stop.
- Replay deterministically covers success and every cancellation path.
- Fence release is proven for every success/failure/cancellation path with no telemetry deadlock.
- No native input API is referenced.

## M7: Foreground Windows Input

Status: Planned

Goal: Add the narrow native boundary that sends configured combat keys.

### Work

- Add a Windows-only `BotDs.Input` project.
- Add strict key parsing and virtual-key mapping.
- Bind every source generation to an exact PID, process-start identity, and HWND, including optical capture sources.
- Add immediate pre-dispatch foreground ownership, `GameInputReady`, and all-chord-keys-up validation plus post-dispatch focus-race detection.
- Add one batched no-held-key `SendInput` chord with complete return-count checking and compensating key-up cleanup for every possibly owned key after partial sends.
- Add configurable global emergency-stop hotkey registration and cleanup.
- Reject emergency-hotkey/profile-binding collisions and unknown or pre-held chord keys.
- Add an injected Win32 facade and dedicated local test-window executable in C#.
- Add cancellation, partial-send, focus-race, process-exit, and shutdown tests.
- Add timed races for chat/modal readiness and user-held chord keys changing between the final check and native dispatch; detected races latch output and residual undetectable windows are reported.
- Do not activate windows or send background/mouse/text input.
- Add emergency-hotkey, focus, native-input, and input-metric dashboard/settings views as a vertical slice.

### Exit criteria

- BotDs never calls `SendInput` when the immediate foreground PID/process-start/HWND precondition fails.
- The dedicated test window receives one expected complete chord when the precondition passes.
- Pre-held/unknown chord keys and emergency-hotkey collisions block dispatch and surface readiness reasons.
- Focus loss pauses output and requires a new snapshot/re-evaluation after focus returns.
- Process exit, detected focus race during dispatch, partial native send, hotkey conflict, cleanup failure, or emergency stop latches output off.
- Shutdown leaves no registered hotkey or BotDs-owned modifier; cleanup failure remains a visible latched fault.

## M8: Closed-Loop Live Combat

Status: Planned

Goal: Enable live output incrementally and prove observed acknowledgement.

### Work

1. Add a control-authorized `Live` output-mode transition with complete readiness checks. Startup always coerces output to `Disabled`, even if `Live` was previously selected.
2. Require disarmed state to change output mode and expose the transition through the dashboard control slice.
3. Run the real Warrior profile in dry-run while manually comparing decisions to expected actions.
4. Calibrate one key at a time and record `Unverified`, `Verified`, or `Mismatch` against the observed ability transition for the active binding generation.
5. Require binding verification or controlled re-verification on every live arm. Do not change action-bar/key mappings while armed.
6. Enable one verified ability and require its typed post-dispatch acknowledgement.
7. Verify no duplicate sends through cooldown/GCD transitions.
8. Add abilities one at a time while preserving one-action-in-flight semantics.
9. Add profile-declared aura/resource/combat-event acknowledgement only when proven and required.
10. Treat unexpected manual ability input while armed as an external-action conflict and stop.
11. Exercise target death, target replacement, no target, friendly target, player death, chat/modal focus, alt-tab, addon reload, source stall, profile mismatch, and emergency stop.
12. Tune only profile data and measured generic timing settings; do not introduce Warrior engine branches.

### Exit criteria

- Each intended key produces one matching observed acknowledgement.
- No unrelated cast, cooldown, aura, or resource change acknowledges an action.
- No duplicate unacknowledged key is sent.
- Target change or death cancels the pending action.
- Missing acknowledgement stops within the configured timeout.
- Live mode cannot activate without control authorization, current readiness, registered emergency hotkey, and verified bindings; restart returns to disabled.
- A 30-minute soak includes at least 200 acknowledged dispatches and every forced-live-failure scenario in `PLAN.md` section 15.1.
- The soak records no stale-state, failed-precondition, duplicate, or runaway dispatch and no unhandled detected focus or target-change race. The documented non-atomic target/focus residuals remain explicit acceptance limitations.

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
