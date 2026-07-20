# BotDs Formal Implementation Plan

Status: Active. M1 transport selected (V5 process memory). M3 snapshot assembly complete. M4 settings and dashboard in progress.

Last updated: 2026-07-20

## 1. Final Outcome

BotDs will be a fully working, high-performance, low-latency, combat-only bot for the Windows x64 RIFT client.

The completed system will:

- Observe the local player and the player's current selected target.
- Distinguish hostile NPCs, hostile players, friendly targets, dead targets, and no target.
- Observe current and maximum health, resources, combat state, casts, available abilities, cooldowns, usability, range, and required auras.
- Evaluate versioned, data-driven combat profiles without character-specific engine code.
- Dispatch configured combat keys only after verifying the exact intended RIFT process and foreground window.
- Correlate each sent action with observed cast, cooldown, aura, resource, or combat-state changes.
- Stop output when telemetry, target identity, process identity, profile state, focus, or acknowledgement becomes invalid.
- Expose live state, controls, settings, diagnostics, action history, statistics, and performance metrics through a responsive localhost dashboard.
- Persist local non-secret settings and profiles atomically.
- Run entirely on the local machine without collecting credentials or chat content.

Movement, navigation, pathfinding, target acquisition, target switching, questing, gathering, looting, and inventory automation are not part of final completion.

## 2. Product Principles

1. Reliability before action output.
   Live state must be measured, normalized, freshness-bounded, and replay-tested before keyboard output is enabled.

2. Latest state wins.
   Telemetry is not queued. The runtime keeps the newest complete observation and discards superseded observations.

3. No action outlives its evidence.
   A decision is invalid when its provider session, source generation, profile generation, controller generation, selected target, ability state, freshness, or foreground process changes.

4. One action in flight.
   The runtime does not send another combat action until the previous action is acknowledged, rejected, cancelled, or timed out.

5. Fail closed on unknown state.
   Unknown and known-empty telemetry remain distinct. Missing or incomplete data cannot satisfy a combat condition.

6. Stable engine, replaceable game data.
   RIFT observation details stay in the addon/provider boundary. Character abilities, keys, level ranges, and rotations stay in profiles.

7. Direct personal-tool architecture.
   Use one process, one dashboard, bounded in-memory histories, and local files. Do not add brokers, databases, cloud services, generalized plugins, or high-availability infrastructure.

## 3. Fixed Technical Decisions

| Area | Decision |
| --- | --- |
| Application platform | C# on .NET 10 or newer, Windows x64 |
| In-game observer | RIFT Lua addon using current-client `Inspect.*` APIs |
| State transport | One transport selected by the live validation gate in section 4 |
| Core state | Transport-neutral immutable C# records with explicit knownness |
| Decision model | Deterministic ordered priority rules |
| Profiles | Versioned JSON validated by schema and runtime semantics |
| Progression | Calling, level ranges, required abilities, and discovered capability reconciliation |
| Target policy | Player's current selected live hostile target only |
| Action output | Windows `SendInput` through a narrow foreground-only key sink |
| Input scope | Configured keyboard keys only; no mouse, window activation, background injection, or arbitrary text |
| Action flow | One pending action with observed acknowledgement and no blind retry |
| Manual combat input while armed | Unsupported; unexpected ability transitions stop the action pipeline rather than acknowledging a BotDs action |
| Runtime orchestration | ASP.NET Core host with bounded hosted services |
| Dashboard | Static local frontend plus authenticated REST and SSE endpoints |
| Settings | Validated local settings, editable while disarmed, persisted atomically |
| Metrics | In-process counters, gauges, bounded histories, and latency distributions exposed in the dashboard |
| Logging | Structured local NDJSON with bounded retention |
| Time | Injected monotonic `TimeProvider` for durations and freshness |

## 4. Telemetry Transport Decision Gate

The Lua addon observation surface is selected. M1 is an approved read-only decision experiment. The physical production transport remains unresolved because the current immutable Lua string does not satisfy the V5 stable-memory contract. No live provider implementation begins until this section is resolved and the plan is updated.

### 4.1 Preferred path: stable process-memory publication

Retain the existing V5 Reader only if the current RIFT addon runtime exposes a real mutable storage primitive that can hold one contiguous region at a stable virtual address.

The memory path passes only when live tests prove:

- The address remains stable through at least 10,000 publications, garbage collection pressure, zoning, combat, and a 30-minute soak.
- Writes mutate the same allocation in place.
- Alternating slots and CRC-last publication reject concurrent torn reads.
- No stale allocation is selected as a newer candidate.
- Typical complete publication takes no more than 5 ms at the 95th percentile.
- The Reader maintains at least a 99.9 percent cache-hit rate after attachment.
- Complete live payloads fit the protocol or a new version is designed with measured bounds.
- The exact production snapshot/event strategy, section cadence, and protocol limits are frozen from measured current-client data.

Short-term address stability of an immutable string is not sufficient evidence.

### 4.2 Fallback path: addon-rendered optical telemetry

If no stable mutable storage primitive exists, use an addon-controlled optical protocol. Evaluate the existing ChromaLink approach first; implement a BotDs optical decoder only if the existing implementation cannot satisfy the required contract.

The optical path passes only when live tests prove:

- A deterministic region of interest can be located and calibrated.
- Captured colors remain distinguishable under the supported display configuration.
- Session, sequence, schema, payload length, and CRC are validated.
- Capture loss, minimization, occlusion, scaling changes, and malformed frames become unusable before the action freshness limit.
- The addon remains visible and updateable during combat and secure mode.
- Source-to-normalized-snapshot latency stays within the performance budget.
- Each capture generation is bound to one exact RIFT top-level window, PID, and process-start identity.

### 4.3 Selection rule — DECIDED

**V5 process memory selected** (2026-07-20). The 360madden/Reader reference tool proved the approach works with the same sentinel-based scanning and ~95% cache hit rate. Our implementation uses 3-tier scanning: cache hit validation, small-window rescan (±2MB direct chunk reads), and full VirtualQueryEx scan.

The optical fallback path is archived. Memory stability is maintained by the GC/allocator behavior confirmed through the conformance probe addon.

Arbitrary internal game-memory reconstruction, process writes, injection, and native in-game modules are not implicit fallbacks. They require a separate architecture decision.

## 5. Runtime Architecture

```text
RIFT Inspect APIs
    -> BotDsBridge observer
    -> selected telemetry transport
    -> ITelemetrySource
    -> TelemetryReaderLoop
    -> HeartbeatSnapshotAssembler
    -> latest ObservationSnapshot
       |-> CombatEvaluator
       |-> ActionCoordinator -> ForegroundProcessGuard -> WindowsKeySink
       `-> Dashboard snapshot and metrics stream
```

### 5.1 Addon responsibilities

- Observe player and `player.target` state.
- Enumerate abilities and auras with complete-list semantics.
- Preserve unknown versus known-empty values.
- Publish session, sequence, client version, frame time, schema version, and completeness.
- Emit no profile logic, key bindings, action decisions, or dashboard behavior.
- Drop an oversized or incomplete publication rather than publishing misleading partial state.

### 5.2 Telemetry source responsibilities

- Attach to or capture the selected source.
- Validate framing, integrity, version, sequence, session, and bounds.
- Produce structured failure codes and privacy-safe metrics.
- Reconnect observation automatically without arming action output.
- Expose a transport-neutral `ITelemetrySource` result.
- Stamp each host read/capture cycle with monotonic start and completion times.
- Provide a short dispatch fence that waits for the current cycle, drains its publication, and prevents a new cycle from starting until dispatch completes.

### 5.3 Snapshot assembly responsibilities

- Publish the newest normalized observation only.
- Track transport liveness separately from game-state evidence freshness.
- Preserve a previous complete section across a transport-only heartbeat only within the same source generation and provider session, without changing that section's evidence sequence or observation time.
- Compute action freshness from the oldest evidence required by the selected rule. A heartbeat never makes old game state actionable again.
- Clear carried state after inspection failure, transport fault, session change, source reattachment, process exit, or incomplete list publication.
- Record source generation separately from wire sequence.

### 5.4 Core responsibilities

- Validate profiles and progression constraints.
- Evaluate one profile against one immutable telemetry snapshot.
- Produce an action intent or a specific waiting, rejection, or stop result.
- Keep all logic deterministic and independent from Windows, process memory, pixels, HTTP, and input APIs.

### 5.5 Action coordinator responsibilities

- Consume only new normalized snapshots while armed.
- Acquire the telemetry dispatch fence, finish/drain the current cycle, apply the newest observation, and then revalidate every source, state, target, input-context, binding, process, window, controller, profile, settings, ability, rule, and rate precondition.
- Capture the comparison baseline/high-water mark only after the drained observation passes revalidation.
- Permit one pending action.
- Enforce global and per-key rate limits.
- Track acknowledgement against later snapshots from the same session, source generation, and target.
- On successful dispatch, record completion and install the pending acknowledgement watcher before releasing the telemetry fence.
- Release the telemetry fence unconditionally in a bounded `finally` path after success, rejection, partial/native failure, emergency stop, cancellation, or shutdown.
- Never retry an unacknowledged key blindly.
- Cancel pending work on disarm, emergency stop, profile change, source change, process exit, or shutdown.

### 5.6 Input boundary responsibilities

- Parse a strict finite key grammar into virtual-key events.
- Confirm the foreground HWND belongs to the captured PID and process-start identity immediately before `SendInput`.
- Confirm every key/modifier in the chord is physically up through the injected keyboard-state facade. A down or unknown key state blocks dispatch.
- Batch one complete key-down/key-up chord with no held-key mode and validate the native return count.
- Recheck foreground ownership immediately after dispatch and latch a fault if focus changed during the non-atomic Windows input window.
- Track any modifier/key-down event that may have been accepted by Windows. On a partial native result, issue compensating key-up cleanup for every possibly owned key even if focus changed, then latch a cleanup fault and send no further combat input. Cleanup key-up events are the only exception to the foreground dispatch rule.
- Never activate a window or send to a background process.
- Register and cleanly unregister a configurable global emergency-stop hotkey.
- Treat hotkey registration conflicts or cleanup failures as readiness failures that block live arming.
- Reject profile bindings that collide with the configured emergency hotkey.

`SendInput` writes to the global Windows input stream. Foreground validation and dispatch are not atomic, so the implementation cannot promise that focus never changes in the intervening scheduling window. The enforceable contract is: BotDs never calls `SendInput` when its immediate pre-dispatch ownership check fails; a detected focus change during dispatch latches output off. This residual race is measured and documented rather than hidden behind an impossible absolute guarantee.

`GameInputReady` and physical key-state checks are also not atomic with `SendInput`. BotDs checks both immediately before dispatch and checks their newest observable state immediately afterward. A detected transition during dispatch latches output off, but an input-context or physical-key change in the unobservable scheduling window can still affect the chord. Timed race tests measure this residual; it cannot be represented as an absolute guarantee.

For optical telemetry, calibration binds the capture region to one exact RIFT HWND, PID, and process-start identity. Capture is limited to the addon-owned region of interest. BotDs does not retain full desktop or game screenshots.

The selected target can also change after final telemetry revalidation but before RIFT processes the injected key. BotDs enforces fresh pre-dispatch target identity and cancels on the first observed post-dispatch target change, but cannot make target selection and global input atomic. Users must not intentionally change targets while an action is pending. This residual race is measured and documented.

## 6. State And Freshness Model

Every actionable observation includes:

- Provider health, protocol/schema version, session ID, sequence, producer time, and received time.
- Source attachment/capture generation.
- Source PID, process-start identity, and top-level RIFT window handle.
- Separate transport-received time and per-section game-state evidence sequence/time.
- Explicit player knownness and target state of `KnownTarget`, `KnownNoTarget`, or `Unknown`.
- Privacy-safe `GameInputReady` knownness that is false for chat/edit focus, key-binding screens, modal UI, loading, or any current-client state proven unable to accept combat keys safely.
- Player identity, health, resources, combat state, and cast state.
- Current target identity, health, relation, player/NPC classification, combat state, and cast state.
- Ability inventory plus explicit inventory knownness.
- Player and target aura lists plus explicit list knownness.
- Secure-mode and client-version information when validated by the current client.

Every action intent captures:

- Controller generation.
- Profile generation and profile ID.
- Source generation, provider session, and source sequence.
- Source PID, process-start identity, and expected foreground window handle.
- Selected target ID.
- Rule ID, ability alias, ability ID, and key.
- Expected acknowledgement and deadline.

The action coordinator discards the intent if any captured identity no longer matches.

## 7. Combat Behavior

The engine evaluates enabled rules in profile order. The first rule whose binding is available for the current level and whose conditions are fully satisfied becomes the action intent.

The baseline condition vocabulary includes:

- Current selected target is live and hostile.
- Target is an NPC or player when the profile specifies that distinction.
- Player and target combat state.
- Player and target health percentage thresholds.
- Resource threshold.
- Resource conditions name an observed resource kind; normalized unit state uses a resource map rather than selecting one hard-coded primary resource.
- Player and target casting state and interruptibility.
- Ability availability, usability, range, and cooldown readiness.
- Required and forbidden player or target auras.
- Binding level range and required/optional capability semantics.

No target, friendly target, dead target, or target loss produces no action. The controller may remain armed in a waiting state while the user selects a valid target.

Build identity is represented by the required ability set until a reliable current-client build/soul identity field is proven. Enabled profiles cannot require an unobservable label.

## 8. Action Acknowledgement

M1 must prove which acknowledgement signals are reliable in the current client, including instant abilities, shared GCD behavior, manual input conflicts, cast events, cooldown events, aura changes, resource changes, and combat events.

Profiles use typed acknowledgement predicates rather than a bare enum:

- Exact ability cast start.
- Ability-specific cooldown transition from the dispatch baseline, excluding a shared-GCD-only transition.
- Aura owner, aura ID, and expected add/remove/stack transition.
- Resource kind, direction, and minimum delta.
- Combat event ability, caster, target, and event kind.

Only predicates proven by M1 and supplied by the selected telemetry contract may be enabled. Unsupported predicates fail profile validation.

Acknowledgement rules:

- The coordinator fences telemetry, drains all in-flight cycles, and captures the comparison baseline and source high-water mark immediately before dispatch.
- After successful dispatch, completion is recorded and the acknowledgement watcher is installed while the fence is still held; the fence is then released.
- Acknowledgement evidence must be newer than the pre-dispatch high-water mark and come from a read/capture cycle that started after dispatch completed.
- M1 must prove that exact instant-action event evidence persists until the first post-fence cycle. If it does not, that acknowledgement type is unsupported.
- It must belong to the same provider session, source generation, and selected target where applicable.
- An unrelated cast, cooldown, resource, or aura change cannot acknowledge the action.
- Timeout stops the action pipeline with a specific reason.
- Timeout does not automatically resend the key.

## 9. Controller And Failure Behavior

| Condition | Required behavior |
| --- | --- |
| No valid selected target | Wait; send nothing |
| Player unavailable or dead | Stop and require explicit recovery |
| Required ability absent or profile mismatch | Stop with a precise diagnostic |
| Unknown required telemetry | Block immediately; stop when required evidence exceeds the configured action-eligible maximum age or the source reports a fault |
| Stale, disconnected, faulted, or corrupt source | Clear pending action and stop |
| Provider session or source generation change | Clear carried state and pending action; never auto-arm |
| Target identity changes | Discard pending action and re-evaluate |
| Game input context is unknown or not ready | Pause output; require a fresh ready observation before re-evaluation |
| Foreground window is not the bound RIFT process | Pause output; revalidate process/window identity and a new snapshot after focus returns |
| Process exits or foreground PID changes during dispatch | Stop and latch a fault |
| Rate limit reached unexpectedly | Send nothing and stop with `RateLimited` |
| Action is not acknowledged | Send nothing further and stop with `ActionNotAcknowledged` |
| Emergency stop | Cancel output immediately and latch `Stopped` |
| Disarm, cancellation, or shutdown | Cancel pending output and release owned resources |

Observation may reconnect automatically. Action output never automatically re-arms after a stopped or faulted state.

Initial non-overridable action bounds are:

- One pending action and burst capacity one.
- Global default 4 dispatches per second, hard maximum 10 per second.
- Per-key default 2 dispatches per second, hard maximum 4 per second.
- At most one dispatch per newly evaluated source sequence.
- Acknowledgement timeout configurable from 100 to 5000 ms.

M1 measurements may lower these ceilings. Raising a hard ceiling requires an explicit plan update and new rate/failure tests.

## 10. Profiles And Progression

Profiles remain versioned JSON with a matching JSON Schema and runtime cross-field validation.

Profile requirements:

- Unique ID and supported profile version.
- Character calling and optional inclusive level range.
- Ability aliases mapped to observed ability IDs and strict key bindings.
- Resource conditions explicitly name a resource kind.
- Required versus optional abilities.
- Per-binding inclusive level ranges.
- Ordered enabled rules with explicit conditions.
- Supported acknowledgement definition for every enabled rule.
- Key-binding verification state of `Unverified`, `Verified`, or `Mismatch` for the active client/profile generation.

Progression behavior:

- Discovered ability inventory is runtime authority.
- Required in-range abilities must be known and present.
- Optional unavailable abilities reject only their dependent rules.
- Out-of-range abilities are inert.
- A profile with no executable rule for the current level fails readiness.
- The engine remains calling-agnostic; the Warrior is only the first real fixture.
- C# runtime validation is authoritative. JSON Schema remains an editor/documentation mirror and conformance tests prevent schema/runtime drift.

RIFT exposes no proven action-bar mapping API. If M1 confirms that limitation, a binding is verified by one controlled key dispatch and an exact observed ability transition. Verification has its own generation and is invalidated by profile/key changes, client/source session changes, or mismatch. Live arming requires current verification or a controlled re-verification step. Action-bar and key-binding changes while armed are unsupported. If a user changes an unobservable binding externally, the first calibration/dispatch may invoke the changed ability before mismatch detection stops output; the dashboard must state this residual limitation.

## 11. Dashboard And Interactive Settings

The dashboard is a complete local control and diagnostic surface, not part of the combat timing path.

### 11.1 Required views

Views are delivered as vertical slices with their owning backend milestones: source/settings in M4, profiles/readiness in M5, actions in M6, native input/hotkey state in M7, and final integration in M9.

- Overview: controller, source, player, target, profile, focus, and readiness.
- Telemetry: freshness, completeness, session, sequence, source generation, and failure details.
- Combat: evaluated rule, rule rejections, pending action, acknowledgement deadline, and recent outcomes.
- Profiles: available profiles, active profile, validation results, reload, and readiness dependencies.
- Profile editor: control-authorized validation and atomic save for local profiles, including recovery when the persisted file is invalid.
- Settings: process selector, source cadence, freshness limit, output mode, rate limits, acknowledgement timeout, emergency hotkey, dashboard update rate, and log retention.
- Metrics: rates, counters, gauges, latency distributions, scanner/capture statistics, evaluator timing, input timing, and error totals.
- Control: arm, disarm, emergency stop, clear stop, and dry-run/live-output selection.

### 11.2 Settings behavior

- Non-secret settings are stored in a local ignored JSON file through atomic replace.
- Settings are validated as one snapshot before activation.
- Combat-affecting settings can change only while disarmed through the existing configuration lease.
- Every settings or profile mutation requires control authorization through explicit endpoint metadata and authorization rules, not route-name conventions.
- Settings declare whether they apply immediately or require source restart.
- Invalid settings leave the previous active snapshot unchanged.
- API and control tokens remain outside dashboard-editable settings.
- Source, profile, runtime-settings, and controller generations are tracked separately. Every relevant change invalidates stale evaluations and action intents.
- Freshness, cadence, rate, timeout, history, and retention settings have non-overridable validated minimums and maximums.

### 11.3 Dashboard isolation

- REST and SSE remain loopback-only and authenticated.
- Kestrel explicitly binds only loopback listeners and rejects wildcard/non-loopback URL overrides; middleware remains defense in depth.
- Dashboard serialization, slow clients, or reconnects cannot block telemetry reading or action dispatch.
- Dashboard updates are throttled independently from combat evaluation.

### 11.4 Responsive and accessible behavior

- Support desktop, tablet, and phone widths from 360 CSS pixels upward.
- Use a multi-column desktop layout, collapsing to one primary column below 768 CSS pixels.
- Keep emergency stop and controller status visible without horizontal scrolling.
- Tables become cards or horizontally contained regions on narrow screens.
- Interactive controls use at least 44 by 44 CSS pixel touch targets.
- All controls are keyboard reachable, visibly focused, labelled, and usable without color as the only state indicator.
- Browser tests cover 360x800, 768x1024, and 1440x900 viewports.

## 12. Performance And Latency Budgets

Initial production targets, subject to current-client measurement. Host latency stages use monotonic timestamps captured at decode/read completion, snapshot publication, evaluation completion, dispatch baseline, and native return. Addon and host epochs are never subtracted directly. M1 measures addon-to-host detection with controlled state transitions and an external monotonic observation procedure or a documented offset calibration with uncertainty bounds.

| Metric | Target |
| --- | --- |
| Addon/source publication cadence | 20-30 Hz |
| Controlled addon state transition to host detection, p95 | 100 ms or less |
| Read/capture completion to normalized snapshot, p95 | 5 ms or less |
| Snapshot publication to evaluation, p95 | 5 ms or less |
| Final safety check plus input dispatch, p95 | 5 ms or less |
| Host detection of actionable state to key dispatch, p95 | 20 ms or less |
| Action-eligible telemetry age | 250 ms default, configurable within 100-500 ms |
| Dashboard update cadence | 5-10 Hz, independent of combat loop |
| Memory Reader cache-hit rate after attach | At least 99.9 percent |
| Full scans during stable operation | Zero |
| Pending action capacity | Exactly one |
| Telemetry/action queues | Bounded latest-only; no backlog |
| Steady-state allocation after warm-up | Less than 1 MB/s excluding explicit recording |
| Working set on the reference machine | Less than 250 MB in steady operation |
| Application CPU on the reference machine | Less than 5 percent total CPU at the configured 20 Hz source rate |

Implementation rules:

- Use event-driven evaluation on new source sequences rather than fixed-delay polling where possible.
- Use bounded channels or equivalent latest-value signaling with capacity one.
- Avoid per-frame large allocations, reflection, synchronous disk I/O, and dashboard work in the combat path.
- Measure before optimizing lower-value code paths.
- Treat any continuous full memory scan or unbounded retry loop as a defect.
- Performance runs record hardware, display mode, source cadence, 60-second warm-up, at least 10,000 samples, p50/p95/p99/max, accepted-frame availability, dropped frames, CPU, working set, allocation rate, and GC counts.

## 13. Statistics And Metrics

Metrics are held in memory, exposed through `/api/status` or a dedicated authenticated metrics endpoint, and rendered by the dashboard.

Required source metrics:

- Attachments, reconnects, cache hits/misses, full scans, bytes scanned, candidate counts, read failures, CRC failures, malformed frames, stale frames, session changes, and source loop rate.
- Capture rate, decode failures, calibration failures, and frame loss if optical transport is selected.
- Current and percentile source read/decode duration.

Required pipeline metrics:

- Snapshot publication rate, frame age, dropped superseded snapshots, heartbeat count, full-frame count, and completeness failures.
- Evaluations per second, evaluation duration, rule matches, per-rule rejection totals, and readiness failures.
- Controller transitions, arm attempts, stops by reason, and emergency stops.

Required action metrics:

- Decisions, dispatch attempts, successful sends, blocked sends by reason, rate-limit hits, acknowledgement success/timeouts, acknowledgement latency, target-change cancellations, focus-loss pauses, and native input errors.
- Foreground ownership-check duration, pre-dispatch revalidation duration, native dispatch duration, cleanup duration, and total host-detection-to-dispatch distributions.
- Bounded recent action records containing monotonic timing, rule/ability identity, outcome, and stop reason without character names or chat content.
- Cancellation totals for every cause, hotkey registration/cleanup failures, modifier cleanup failures, and detected focus races.

Required dashboard metrics:

- Active SSE clients, reconnects, update rate, serialization duration, API failures, and settings validation failures.
- CPU, working set, managed allocation rate, GC counts, dropped logs, replay bytes, and bounded-history utilization.

Metric labels have fixed bounded cardinality. Per-rule displays use the finite active profile rule set and are reset on profile generation changes. SSE clients, action history, replay size, and pending log capacity have explicit configured caps.

Structured NDJSON logging uses a bounded asynchronous channel so evaluator and action paths never wait on disk. When saturated, low-priority diagnostic records may be dropped and counted; controller stops, input failures, and action outcomes have reserved capacity. Retention includes both age and total-size bounds.

## 14. Verification Strategy

Every milestone requires automated regression tests and measured live-client exit criteria.

### 14.1 Automated tests

- Pure Core tests for profile validation, progression, evaluation, acknowledgement, and failure behavior.
- Protocol fixtures for every field, boundary, corruption case, sequence transition, and completeness state.
- Fake transport tests for source lifecycle, reconnect, stale state, process loss, and cancellation.
- Replay tests that produce deterministic snapshots, decisions, actions, acknowledgements, and stops.
- A versioned replay envelope containing normalized observations, monotonic time advances, controller commands, settings/profile generations, process/window/focus events, dispatch outcomes, and source faults.
- App integration tests for hosted services, settings leases, dashboard endpoints, authentication, and SSE.
- Settings/profile persistence tests for crash-before-replace, corrupted startup files, invalid updates, authorization roles, restart-required rollback, and atomic recovery.
- Native input tests through an injected Win32 facade and a dedicated local test-window fixture.
- Concurrency tests for shutdown, emergency stop, focus changes, source replacement, and profile changes during evaluation.
- Browser tests for responsive viewports, keyboard accessibility, SSE reconnect, token roles, settings interaction, and rendered metric updates.

### 14.2 Live-client tests

- Current-client addon API conformance for every consumed field and event.
- Transport integrity, performance, secure-mode, zoning, reload, loading, and long-session behavior.
- Calling/capability identity, no-target versus target-unknown, death events, GCD versus ability cooldown, range/usability unknownness, resource kinds, and typed acknowledgement feasibility.
- Player/target/ability/aura/cast correctness against visible game state.
- Dry-run combat traces before any input is enabled.
- Foreground-only input against a test window before RIFT.
- Live one-action acknowledgement tests before continuous combat.
- A final 30-minute combat soak covering target loss, focus changes, source stall, addon reload, process restart, profile mismatch, acknowledgement timeout, and emergency stop.

### 14.3 Required repository gates

```text
dotnet build BotDs.sln --no-restore
dotnet test BotDs.sln --no-restore
dotnet format BotDs.sln --verify-no-changes --no-restore
node --check src/BotDs.App/wwwroot/js/app.js
luac -p addons/BotDsBridge/BotDsBridge/main.lua
git diff --check
```

## 15. Final Acceptance Definition

BotDs is complete only when all of the following are true:

- The selected telemetry transport passes its live soak and failure tests.
- The dashboard shows correct live player, target, ability, aura, cast, source, focus, profile, and action state.
- Interactive settings persist, validate, apply safely, and cannot race an armed controller.
- A real enabled profile handles the current Warrior without engine hard-coding.
- A synthetic non-Warrior profile proves engine generality.
- The bot casts the intended configured ability against the current selected hostile target.
- Every sent action is bounded, foreground-verified, and acknowledged or stopped.
- No dispatch is attempted when stale/unknown telemetry, target, process, window, focus, controller, profile, or rate-limit preconditions fail. Any detected focus change during the non-atomic Windows dispatch window latches output off.
- Unknown/blocked game input context and unverified/mismatched key bindings prevent live dispatch.
- Dashboard and global emergency stop prevent further input until explicit clear and re-arm.
- Metrics and bounded action history explain source health, decisions, blocked actions, sends, acknowledgements, and stops.
- All automated and live acceptance tests pass.
- Packaging and setup instructions allow the addon and application to be installed and run locally from a clean checkout.

### 15.1 Acceptance matrix

| Area | Minimum workload | Pass criteria |
| --- | --- | --- |
| Transport soak | 30 minutes at 20 Hz after warm-up, at least 36,000 expected publications | At least 99.9 percent accepted availability; all 100 injected corrupt/malformed publications rejected; source loss detected within 500 ms |
| Live field conformance | At least 20 target select/clear/switch cycles; 10 health/resource changes; 10 cooldown/range changes; 10 casts; 10 aura add/remove cycles; 3 zoning or reload cycles | Every transition maps to the expected known/unknown state and evidence timestamp with no stale carry across a fault/session boundary |
| Replay | Every controller command, generation change, focus/process event, source fault, dispatch outcome, and acknowledgement type | Repeated runs produce identical snapshots, decisions, sends, acknowledgements, stops, and metrics |
| Test-window input | At least 1,000 dispatch attempts with scheduled foreground, input-context, and held-key races plus partial-send injection | No combat dispatch when a precondition already fails; every passing attempt produces one complete chord; every detectable injected race/fault latches output and completes ownership cleanup; residual unobservable race frequency is recorded |
| Live combat | 30 minutes and at least 200 acknowledged dispatches | No duplicate unacknowledged dispatch, stale-state dispatch, failed-precondition dispatch, unhandled detected focus race, or unresolved pending action |
| Forced live failures | At least one each: no/friendly/dead/switched target, player death, chat/modal focus, alt-tab, telemetry stall/corruption, addon reload, process restart, profile/settings change attempt, acknowledgement timeout, rate limit, dashboard stop, global emergency stop | Each scenario produces the documented wait/pause/stop result and no further combat dispatch until its explicit recovery condition is met |
| Dashboard | Required desktop/tablet/phone viewports and all control/read token roles | All views, settings, profile edits, metrics, SSE reconnects, accessibility interactions, and authorization checks pass without affecting combat latency |
| Performance | 60-second warm-up plus at least 10,000 measured samples on the recorded reference configuration | All section 12 latency, CPU, memory, allocation, queue, and availability budgets pass at their stated percentile/max bounds |

The final review must report no unresolved critical or high-severity defects. Any accepted lower-severity limitation is recorded with its observable impact and workaround.

## 16. Inputs Required From The User

The implementation can complete telemetry and dry-run infrastructure before these are known. Live profile and input completion require:

- Actual RIFT executable basename or preferred explicit PID workflow.
- Supported window mode, resolution, and UI scale if optical transport is selected.
- Current character ability IDs and exact names observed from telemetry.
- Action-bar keys for each bound ability.
- Ordered combat rules and thresholds.
- Required buff/debuff IDs and expected acknowledgement changes.
- Preferred global emergency-stop key chord.
- Approval to transition output mode from disabled to dry-run and then to live after each gate passes.
