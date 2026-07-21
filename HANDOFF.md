# Implementation Handoff

Last updated: **2026-07-21k** (authoritative resume snapshot)

**Repo:** `C:\work\bot-ds` · **Branch:** `master` · **Last committed HEAD:** `a0a8be4` (large uncommitted working tree — do not discard; user has not requested commit)

## Resume in one paragraph

BotDs is a personal RIFT combat-only bot (C#/.NET 10 + Lua bridge). **M0–M7 complete; M2/M8 code-complete** with live residual deferred; **M9 planned**. Live path has been proven Healthy (player Atank L45 warrior, abilities inventory). Bridge **0.2.0** / protocol **schema v2** fixes ability usable/CD/name (Detail times are **seconds**; usability is **`not unusable`**). Offline work added draft authoring tests, DryRun multi-tick harness, and file-backed replay. **Do not invent Warrior ability IDs, keys, or rotations.** Next product value is **live return**: `/reloadui` → confirm abilities/bar → confirm draft keys → DryRun; then Live residual / M9 as needed.

## RIFT addon path (read this first — agents keep getting it wrong)

| Item | Value |
|------|--------|
| **Shell MyDocuments** | `Environment.SpecialFolder.MyDocuments` → `C:\Users\mrkoo\OneDrive\Documents` |
| **Player AddOns** | `{MyDocuments}\RIFT\Interface\AddOns\` |
| **BotDsBridge deploy** | `{MyDocuments}\RIFT\Interface\AddOns\BotDsBridge\` |
| **Code** | `BotDs.Core.RiftLocalPaths` |
| **Doc** | `docs/rift-local-paths.md` |
| **Deploy command** | `deploy-addon.cmd` |
| **Bridge version** | **0.2.0** (`RiftAddon.toc` + `ADDON_VERSION` in `main.lua`) |

**Wrong:** Glyph `Live\Interface\Addons`, or non-OneDrive `C:\Users\...\Documents\RIFT\...` when MyDocuments is OneDrive.  
**Verify:** destination parent must already list JAB/ReaderBridge/etc. before deploy is “done”.  
**TOC:** non-empty `Identifier`, `Name`, and `Email` required or RIFT never lists the addon (`Email = ""` is fatal).  
**Then:** in-game `/reloadui` — chat should show `BotDs Bridge v0.2.0`.

## How to run

| Mode | Command |
|------|---------|
| Dev | `run-botds.cmd` (default process `rift_x64`, URL `http://localhost:5068`) |
| Publish | `dotnet publish src/BotDs.App/BotDs.App.csproj -c Release -r win-x64 --self-contained false -o publish/` then `run-botds.cmd --publish` |
| Addon | `deploy-addon.cmd` then in-game `/reloadui` |
| First-run doc | `docs/first-run.md` |

- Dashboard: **loopback only**, **no API tokens**.
- Profiles: `profiles/` (absolute pin via `BotDs__Profiles__Directory` / launcher). Sidecar `*.names.json` is authoring-only (not loaded as combat profiles).
- Published exe name: **`publish\BotDs.App.exe`** (not `BotDs.exe`).

## Resume instruction (next agent)

1. Read `AGENTS.md`, `docs/rift-local-paths.md`, `PLAN.md`, `ROADMAP.md`, `docs/first-run.md`, this file.
2. Assume **uncommitted work is intentional** — preserve it; do not `reset --hard` or force-push.
3. Verify offline: `dotnet test BotDs.sln` (expect **579** as of this handoff; re-count after changes).
4. If RIFT is available, follow **Live return checklist** below before inventing more offline work.
5. Constraints: C#/.NET 10 only for new tools; no Python; no standalone `.ps1` apps; privacy/loopback; no movement/pathfinding.

## Live return checklist (highest product priority)

1. RIFT running as **`rift_x64`**; `deploy-addon.cmd` already targets OneDrive AddOns — confirm sibling addons present.
2. In-game **`/reloadui`** → `BotDs Bridge v0.2.0`.
3. Start app: `run-botds.cmd` → open `http://localhost:5068`.
4. `GET /api/status` → **Healthy**, player/target knownness sensible.
5. Dashboard **Abilities**: names present (schema v2), usable not permanently all-false; cast and watch CD ms change.
6. Dashboard **Action bar**: slots → ability ids; suggested keys are **defaults only** — operator confirms.
7. **Draft profile from live** → confirm keys in `profiles/draft-*-live.json` → enable carefully.
8. Hostile target → profile selected → output **DryRun** → decisions should match offline multi-tick expectations (`DryRunMultiTickTests` pattern).
9. Live mode only after binding Verified + emergency hotkey; never invent keys.

## Milestone status

| Milestone | Status |
|-----------|--------|
| M0–M7 | **Complete** |
| M2 live telemetry | **Code-complete** + live Healthy proven earlier; soak / formal §15.1 residual deferred |
| M8 closed-loop combat | **Code-complete**; live residual deferred (calibration, soak, failure matrix) |
| M9 packaging/acceptance | **Planned** (publish/launcher exist; docs improved; not full acceptance) |

**Do not** mark M2/M8 Fully Complete without live soak evidence. **Do not** invent Warrior combat content.

## Architecture snapshot (shipped)

- **Core:** `TelemetryFrame`, knownness, profiles, `CombatEvaluator`, ack/binding/external-conflict, `ReplayEnvelope` + **`ReplayEnvelopeStore`** (file load/save, size/version limits).
- **Reader:** V5 dual-buffer, dual **schema 1–2** (v1 46-byte abilities; v2 80-byte + name + action bar), RankByFreshness, limit-only partial select, benign sequence gaps ≤15 Healthy, proactive window refresh (no full-scan on stagnation).
- **App:** loopback dashboard (no tokens), `TelemetryReaderLoop`, evaluator/coordinator DryRun/Live, emergency hotkey, **DraftProfileBuilder**, `/api/abilities`, `/api/action-bar`, draft-from-telemetry, abilities/bar UI cards.
- **Addon:** BotDsBridge 0.2.0 — materialize with sequence, Version table, seconds→ms CDs, usable from unusable, action bar section.
- **Profiles:** `draft-warrior-45-live.json` (55 observed IDs, disabled, empty keys); `disabled-warrior-45.json` placeholder; `synthetic-mage-50.json` test fixture only.

## Verification status

Last full offline run (session 2026-07-21j):

```text
dotnet test BotDs.sln   → 579/579 pass
```

Key new/updated tests:

- `AbilityFidelityTests` — usable/CD/name map, schema v1 ability records, action bar
- `DraftProfileAuthoringTests` — key hints, names sidecar content, disabled validate
- `DryRunMultiTickTests` — multi-tick rule fire using real draft ability IDs
- `ReplayEnvelopeStoreTests` + fixture `tests/BotDs.Tests/Fixtures/replay-combat-cycle.json`
- Existing M8/coordinator/scanner suites remain

## Remaining work (ordered)

### When live (product-critical)

1. `/reloadui` + Healthy end-to-end with schema v2 names/CD/bar.
2. Operator key confirmation on draft profile; DryRun on hostile target.
3. Binding verification + Live residual (ack soak, failure matrix, 30‑min combat soak).
4. Optional: capture a live `ReplayEnvelope` fixture from real frames for regression.

### Offline (if still offline)

1. Phase 4 deferred: thin `WebApplicationFactory` tests for `/api/abilities`, action-bar, draft POST, control fail-closed.
2. Further M9: clean-checkout doc polish, publish smoke assert (exe + wwwroot), knowledge.md/README residual cleanup (README limitations were updated 2026-07-21j; some sub-READMEs may still be stale).
3. Findings-only bug hunt (high reasoning) on scanner/coordinator before Live — not required to re-green suite.

## Hard constraints (always)

- C# / .NET 10+ for new executable helpers; no Python; no standalone PowerShell apps (`.cmd` thin wrappers only).
- Privacy: local, loopback control, no credentials/chat collection.
- No movement/pathfinding.
- No inventing Warrior ability IDs, keys, or rotations.
- Fail closed on stale/unknown/ambiguous combat state.

## Agent runtime note (historical)

An earlier OpenCode swarm hit a SQLite schema mismatch on old `1.15.3`; package was upgraded to `1.18.3`. Restart OpenCode after agent config changes. See prior notes / upstream as needed; not a product blocker for Grok Build / direct `dotnet` work.

## Agent Runtime Status (legacy)

An earlier five-agent swarm exposed an OpenCode runtime/database schema mismatch. The process used OpenCode `1.15.3`, while the shared SQLite schema required `session_message.seq`.

The database passed `PRAGMA integrity_check`, and the global package was upgraded to OpenCode `1.18.3`. Subsequent parallel agent, integration, and review sessions completed successfully. Restart OpenCode after changing project agent configuration because configuration is loaded only at process startup.

Related upstream report: <https://github.com/anomalyco/opencode/issues/31204>

## Approved Product Plan

The user approved implementation of a Windows x64 combat-only RIFT bot with:

- C# and .NET 10 for the application and all new executable helpers.
- A Lua addon component that publishes structured game state for the external Reader subsystem.
- A high-performance, low-latency local telemetry provider. The existing integrity-checked V5 memory Reader is the preferred candidate, pending the formal M1 transport gate.
- Current player-selected hostile target only; no target acquisition or switching.
- Versioned JSON combat profiles and no Warrior-specific engine logic.
- A level-45 Warrior only as the first data fixture.
- Foreground RIFT action output through configured key bindings.
- A localhost ASP.NET Core dashboard with live state and full local controls.
- Minimal structured local logs, monotonic durations, useful error boundaries, and enough action history to diagnose personal use without collecting credentials or chat content.
- Privacy remains a requirement: loopback-only control, no credential capture, no chat-content collection, and no unnecessary sensitive identifiers in logs.
- Movement, pathfinding, and navigation remaining out of scope.

The formal architecture and completion contract is `PLAN.md`; ordered milestones and exit criteria are in `ROADMAP.md`.

## Repository Constraints

- All new executable helpers, utilities, generators, and maintenance tools must be C# targeting .NET 10 or newer.
- Do not add Python helpers or scripts.
- Do not add standalone PowerShell applications or `.ps1` files.
- `.cmd` files may be thin convenience wrappers; application logic belongs in C#.
- Preserve unrelated user or agent changes.
- Do not commit or push unless explicitly requested.

### Personal Tool Workflow

- Optimize for one local operator, direct functionality, and straightforward maintenance rather than commercial-product operations.
- Prioritize the user's requested functionality, reliability, performance, maintainability, local control, and privacy.
- Retain technical input controls that prevent accidental interaction with the wrong process or stale state.
- Prefer one concise local diagnostic stream over separate application, action, and audit infrastructures unless a concrete debugging need appears.

## Completed Work

### Repository And Solution

- Added `global.json` pinned to SDK `10.0.204` with latest-patch roll-forward.
- Added `Directory.Build.props` with latest language features, deterministic builds, current analysis, and warnings as errors.
- Added `.gitignore` for .NET, IDE, test, log, and Playwright output.
- Created `BotDs.sln`.
- Created `src/BotDs.Core` targeting `net10.0`.
- Created `src/BotDs.Reader` targeting `net10.0-windows` with unsafe blocks enabled.
- Created `src/BotDs.App` targeting `net10.0-windows` with ASP.NET Core.
- Created `tests/BotDs.Tests` targeting `net10.0-windows` with xUnit.
- Added project references among Core, Reader, App, and Tests.
- Added `Serilog.AspNetCore` `10.0.0`; its package graph includes console, file, compact JSON, configuration, and hosting support.

### Core Implementation

`src/BotDs.Core` currently contains:

- `StateModels.cs`: provider health, controller states, stop reasons, player/target state, health/resources, casts, abilities, auras, and telemetry frames.
- `CombatProfiles.cs`: versioned profile records, bindings, ordered rules, conditions, acknowledgement types, JSON loading, and semantic validation.
- `CombatEvaluator.cs`: deterministic ordered evaluation, profile/character checks, current-hostile-target checks, ability readiness, resource/health/aura/cast conditions, action decisions, and rule rejection reasons.

This code has received parallel implementation, integration, adversarial testing, and findings-only review. The remaining limitations are listed below rather than hidden behind placeholder behavior.

### Context And Research

- Historical RIFT automation evidence is under `context/`.
- AutoHotkey forum sources were recovered through Playwright and summarized.
- `C:\work\LLM_RIFT_API` was reviewed and summarized in `context/rift-addon-api-corpus.md`.
- The current Reader repository was inspected at commit `5a549e01fd6279b6a398994bf259ba1a89103e5d`.
- Reader's C# v4 parser contains phase-two models that its Lua emitter does not currently publish; do not assume the existing v4 implementation is internally complete.

## Verification Status

Authoritative recent suite (2026-07-21j offline handoff):

```text
dotnet test BotDs.sln   → 579/579 pass
```

Full formal gate (when time allows):

```text
dotnet restore BotDs.sln
dotnet build BotDs.sln --no-restore
dotnet test BotDs.sln --no-build
dotnet format BotDs.sln --verify-no-changes --no-restore
node --check src/BotDs.App/wwwroot/js/app.js
```

## Configured OpenCode Agents

The configured swarm has been exercised successfully. Project-local definitions are under `.opencode/agents/`.

| Agent | Model | Intended Role |
| --- | --- | --- |
| built-in `general` | `opencode-go/deepseek-v4-flash`, high | Fast general implementation |
| built-in `explore` | `opencode/mimo-v2.5-free` | Free codebase exploration |
| `architect` | `openai/gpt-5.6-sol`, xhigh | Read-only architecture decisions |
| `integrator` | `openai/gpt-5.6-sol`, high | Cross-project integration |
| `protocol-engineer` | `opencode-go/deepseek-v4-pro`, max | Reader, protocol, native interop, and Lua contract |
| `reviewer` | `openai/gpt-5.6-sol`, high | Read-only final review |
| `core-worker` | `opencode-go/deepseek-v4-flash`, high | Reasoned isolated C# implementation |
| `test-worker` | `opencode-go/deepseek-v4-flash`, high | Adversarial tests and failure-path reasoning |
| `simple-worker` | `opencode/mimo-v2.5-free` | Free mechanical edits and established-pattern changes |
| `dashboard-worker` | `opencode/mimo-v2.5-free` | Free dashboard implementation |
| `researcher` | `opencode/nemotron-3-ultra-free` | Free read-only research |

Model-routing requirement:

- GPT models are reserved for the high-reasoning architecture, integration, and final-review roles.
- Non-GPT agents use OpenCode Go models when implementation reasoning is required and catalog-free models for bounded work.
- This routing rule does not apply to external harnesses such as Codex, Freebuff, or Grok Build.

The merged configuration and agent definitions passed `opencode debug config` and direct model smoke tests on 2026-07-19. `opencode/north-mini-code-free` and OpenCode Zen's `opencode/gpt-5.6-sol` returned `Model is disabled`, so affected agents were moved to the active routes above. Agent configuration is loaded only at process startup.

## Implemented Baseline After Swarm

- Core profile loading is fail-closed, supports explicit profile disablement, and rejects malformed semantic state.
- `schemas/combat-profile.schema.json` and a disabled level-45 Warrior placeholder exist without invented game data.
- Reader v5 defines an integrity-checked double-buffer contract, CRC32, bounded parser, continuity/freshness tracking, and Core telemetry mapping.
- Reader now includes explicit PID/exact-name process selection, minimal-rights Windows x64 attachment, readable-region enumeration, chunked V5 sentinel discovery, protocol-valid candidate ranking, cache invalidation/relocation, structured failures, and privacy-safe scanner metrics.
- Scanner selection fails closed on incomplete scans, ambiguous candidates, partial reads, stale cached regions without a newer replacement, process loss, and cancellation. Native and sparse-memory tests do not require a live RIFT process.
- The Lua bridge emits the provider envelope and heartbeat using Lua-compatible arithmetic; live unit, ability, and aura population still requires current-client conformance work.
- The App hosts authenticated localhost status/profile/control/SSE endpoints, structured JSONL logs, evaluator plumbing, and a responsive static dashboard.
- SSE uses authenticated `fetch()` streaming; tokens are not placed in URLs. Empty configured tokens fail closed.
- No keyboard or other game-action output exists.
- Provider freshness now ages monotonically at the snapshot boundary, and stale evaluations are rejected across lifecycle and profile-configuration generations.
- V5 parsing now enforces exact section masks, uniqueness, ordering, schema version, heartbeat contents, and exact section-body consumption.
- Unknown health and omitted aura telemetry fail closed; explicit empty aura sections remain distinguishable from unknown state.
- Dashboard API locality uses the remote loopback address, and profile reload requires control authorization.
- Dashboard arming confirms current technical readiness.
- Profile reload now preserves the previous cache and active profile on missing directory, invalid JSON, or semantic validation failure — reload is fully atomic.
- Profile validation details are bounded to 120 characters, and load failures omit exception text and absolute paths.
- Profile validation rejects non-blank `character.build` on enabled profiles and enforces structural requirements: at least one enabled binding, at least one enabled rule, no rules referencing disabled bindings, and level-range overlap between enabled rules and the profile's level range.
- Evaluator required-binding reconciliation: when `IsAbilitiesKnown` is false and the profile has in-range required bindings, evaluation stops with `ProviderUnavailable`. When a required ability is missing from telemetry, evaluation stops with `ProfileMismatch`. Optional missing abilities are handled as rule rejections without stopping. Level-range awareness ensures out-of-range required bindings are inert.
- Telemetry frame now carries `IsAbilitiesKnown`, populated by the V5 mapper: an omitted abilities section mask yields `false`, a present (even empty) abilities section yields `true`.
- Scanner stale backoff suppresses repeated full rescans within a 5-second window after a stale result, preventing busy loops when the region is permanently stale. Backoff resets on reattach.
- Scanner deduplication detects ambiguous candidates when same-session same-sequence frames have different flags, protocol versions, heartbeat intervals, or payload lengths.
- `ReadExact` enforces address bounds: overflow, max-application-address, and negative-address cases throw `ReaderFailureCode.ReadFailure`.
- `CandidateLimitHits` metric incremented only for limit-exceeded failures (not for `QueryFailure`).
- Controller adds `ClearStop` to release the explicit `Stopped` latch, returning to `Disarmed`.
- Dashboard UI adds a two-checkbox arm confirmation dialog (target + system readiness), a Clear Stop button, and a token clear button.
- `WindowsMemoryReader.IsRangeWithinApplicationBounds` uses inclusive maximum (native-maximum-sized reads validate correctly).
- Native error mapping (`MapWin32Error`) wired for `VerifyName` failures, with test coverage.
- `schemas/combat-profile.schema.json` updated with `character.build` condition, `required` per-binding field, and extended conditions.
- `addons/BotDsBridge/PROTOCOL.md` contains the normative wire-format specification with byte offsets, section encoding, CRC rules, double-buffer discipline, and health mapping.
- The solution currently has 544 passing Core, protocol, scanner/native, controller, security, profile, telemetry, pipeline, evaluator, acknowledgement, and action-coordinator tests.

Last verified on 2026-07-21 (Session 2026-07-21j):

```text
dotnet test BotDs.sln   → 579/579 pass
```

## Recommended Swarm After Restart

Launch independent work concurrently with strict path ownership:

| Agent | Ownership |
| --- | --- |
| `protocol-engineer` | `src/BotDs.Reader/**` and `addons/BotDsBridge/**` |
| `dashboard-worker` | `src/BotDs.App/wwwroot/**` |
| `general` | App host/services under `src/BotDs.App/**`, excluding `wwwroot` |
| `test-worker` | `tests/BotDs.Tests/**` |
| `core-worker` | Core review/fixes plus `profiles/**` and `schemas/**` when explicitly assigned |
| `simple-worker` | Mechanical edits and established-pattern changes under explicitly assigned paths |

Do not let concurrent agents edit the same project file. Let the primary agent perform package/project-file changes. After workers finish, use `integrator` for the full build and `reviewer` for findings-only final review.## M1 Transport Gate Findings (2026-07-20)

### V5 Process Memory Transport: VIABLE ✅

The 360madden/Reader tool (`https://github.com/360madden/Reader`) uses the same sentinel-based memory scanning architecture with a ~95% cache hit rate. Our V5 scanner already implements cache-hit + full-scan (small-window rescan is a future optimization). **No architectural changes needed.**

Despite Lua strings being immutable and the addon rebuilding the buffer on every frame, addresses tend to remain stable because:
- GC runs incrementally, not every frame
- Allocator reuses recently-freed same-size slots
- Fixed 16,400-byte allocation is predictable
- No competing large allocations in the addon

### SavedVariables as Scaffolding
- Flush only on `/reloadui` or client exit (NOT periodic)
- File path: `{MyDocuments}\RIFT\SavedVariables\[Account]\[Shard]\[Char]\BotDsBridge.lua` (shell MyDocuments / OneDrive — see `docs/rift-local-paths.md`)
- Useful for: debug payload inspection, API surface exploration, config persistence
- Not suitable for real-time telemetry

### RIFT Environment Facts
| Fact | Detail |
|------|--------|
| Process names | `rift.exe`, `rift_x64.exe` |
| Lua runtime | Standard Lua (NOT LuaJIT), sandboxed |
| `print()` output | In-game console only (NOT client log) |
| `collectgarbage()` | Restricted in sandbox |
| Chat focus detection | `UI.Textfield.Focus(nil)` or `Event.UI.Focus.Gain/Lose` |
| Secure mode | `Inspect.System.Secure()` returns boolean; poll via Update.Begin |
| Combat events | `Event.Combat.Damage/Death/Dodge/Healing/Miss/Parry/Resist` with caster/target/ability/amount |

### Action-Bar Mapping
- `Action.Get(slot)` returns ability placement — **observable**
- `Action.Bar.Page.Get()` returns current page — **observable**
- Key bindings to slots — **NOT observable**
- Conclusion: bindings must be user-configured in profiles, verified via controlled calibration

### M1 Artifacts Created
| Artifact | Path |
|----------|------|
| Conformance probe addon | `addons/BotDsConformance/` |
| Field/event matrix (awaiting live data) | `docs/m1-field-event-matrix.md` |
| Stable storage finding | `docs/m1-stable-storage-finding.md` |
| Populated live addon (game-state sections) | `addons/BotDsBridge/BotDsBridge/main.lua` |
| Hosted scanner loop | `src/BotDs.App/Services/TelemetryReaderLoop.cs` |
| Scanner dashboard card | `src/BotDs.App/wwwroot/index.html` + `js/app.js` |## Session 2026-07-20b: Pipeline & Evaluator Integration Tests

### Commits (7 total this session)

| Commit | What |
|--------|------|
| `20db66c` | UI bug hunt: 7 CSS/HTML fixes (coordinator full-width, settings form CSS, badge overflow, mobile responsive) |
| `e9162f2` | JS audit: 8 bug fixes (sessionStorage crash, apiFetch 204 crash, null DOM refs, race conditions, CSS class sanitize) |
| `bcb3f2e` | 8 pipeline integration tests (scanner → loop → publisher chain) |
| `210bc4b` | Player data propagation test + V5 UnitState binary encoding helpers |
| `86bfd2f` | Target data + heartbeat carry-forward pipeline tests |
| (latest) | 9 EvaluatorLoop integration tests (telemetry → evaluation → coordination bridge) |
| `89b540c` | Refactor: extract shared BuildSlotWithUnit, eliminate 45 lines of duplication |
| `bd4e96b` | ActionCoordinator startup-Disabled enforcement test (M8 safety gate) |

### New Test Files Created

- `tests/BotDs.Tests/PipelineIntegrationTests.cs` — 11 tests covering scanner→publisher chain, player data, target data, heartbeat carry-forward, process exit, fault handling
- `tests/BotDs.Tests/EvaluatorLoopTests.cs` — 9 tests covering disarmed/stopped skip, emergency stop on null profile, evaluation with coordinator delegation, fault handling, cancellation, generation guards, multi-frame evaluation

### New V5 Binary Helpers (in ScannerTestHelpers)

- `BuildUnitSection(...)` — encodes ParsedUnitState into V5 binary wire format matching V5Parser.ParseUnitState (16 fields in parser order)
- `BuildSlotWithPlayer(...)` — complete V5 slot with ProviderInfo + Player sections
- `BuildSlotWithTarget(...)` — complete V5 slot with ProviderInfo + Target sections
- `BuildSlotWithUnit(...)` — shared implementation extracted from the above two
- `WriteSectionHeader(...)`, `WriteLenPrefixedAscii(...)`, `WriteLenPrefixedUtf8(...)` — low-level V5 encoding helpers

### Architecture Status

All M0-M7 milestones are complete. M8 (Closed-Loop Live Combat) is active. Code-level work remaining:

- Profile-declared acknowledgement validation (verify unsupported AcknowledgementKind values rejected at profile load time)
- Any missing edge-case tests discovered during live testing

### What Requires Live RIFT Client

Items 3-12 of M8 require the game running with the BotDs addon: key calibration, binding verification, typed acknowledgement testing, failure scenario exercise, and 30-minute combat soak. The code infrastructure is ready.

## Remaining Implementation

Authoritative detail is in `ROADMAP.md`. Summary: **M0–M7 complete; M2/M8 code-complete + live residual; M9 planned.**

**Deferred live-only (blocks full M2/M8 Complete):**
1. `/reloadui` with bridge 0.2.0 → stable Healthy + schema v2 names/CD/bar
2. Dry-run Warrior comparison with **confirmed** keys (draft IDs already observed)
3. Key calibration / binding verification soak
4. Typed acknowledgement soak in live client
5. Forced-failure matrix + 30-minute combat soak (≥200 acks)
6. Combat-event acknowledgement section (protocol gap)
7. M9 full acceptance (docs/publish partial; soak and browser matrix not done)

## Session 2026-07-21k: Handoff refresh

Rewrote top-of-file resume snapshot: run commands, live checklist, milestone table, 579 tests, uncommitted-tree warning, next-step order. No product code change in this flush.

## Session 2026-07-21j: Offline plan Phases 1–3 (+ packaging docs)

Optimal offline order executed while RIFT unavailable:

1. **DraftProfileBuilder** pure path + `DraftProfileAuthoringTests` (key hints, names, validate disabled)
2. **DryRunMultiTickTests** — multi-tick fire/no-fire with real draft ability IDs
3. **ReplayEnvelopeStore** load/save + size/version guards; fixture `tests/BotDs.Tests/Fixtures/replay-combat-cycle.json`
4. Phase 4 host tests **deferred** (time-box); Phase 5 docs: `docs/first-run.md`, README/HANDOFF publish path fix

**Live return:** `/reloadui` → Healthy abilities/bar → draft → confirm keys → DryRun against multi-tick expectations.

## Session 2026-07-21i: Dual-schema + dashboard abilities/bar

### Delivered
- Parser accepts **schema 1 and 2** (v1 46-byte abilities without name; v2 80-byte + name + action bar) so live keeps working until `/reloadui`
- Dashboard cards: **Abilities** table + **Action bar** (slot / suggested key / ability id)
- **Draft profile from live** button; draft key hints from bar slots `1–12 → 1–0,-,=` (confirm in-game)
- Suite **559/559**

### Operator
1. Prefer `/reloadui` for bridge **0.2.0** (names + honest CD ms + bar section)
2. Use dashboard Abilities/Bar for calibration; fill remaining keys; DryRun only after confirm

## Session 2026-07-21h: P0–P3 ability fidelity → DryRun → authoring → residual

### P0 Ability field fidelity
- Root cause: RIFT Detail times are **seconds**; bridge previously wrote raw values / treated as ms → CD always null; `usable` member is not the Detail signal — use **`not unusable`**
- Schema **v2** ability records **80 bytes** with display **name**
- Unit tests: `AbilityFidelityTests` (parse→map usable/CD/name + evaluator fire/no-fire)

### P1 One-ability DryRun path
- Shipped evaluator tests prove fire when usable+CD ready, no-fire on CD
- Draft profile rule skeleton uses `abilityUsable` / `cooldownReady` / `targetHostile` (disabled + empty keys)

### P2 Authoring
- `GET /api/abilities` names; draft writes `.names.json` sidecar
- **Action bar** section 0x0007 + `GET /api/action-bar` (slot→abilityId; **no keys invented**)

### P3
- Multi-ability rule order test; Live fail-closed suite still green
- `dotnet publish` → `publish/`; `run-botds.cmd --publish`
- Soak residual: see honest residual note (not M8 Fully Complete)

### Operator
1. `/reloadui` for bridge **0.2.0**
2. Confirm `/api/abilities` shows names + non-null CD when casting
3. Fill keys in draft profile before DryRun/Live

## Session 2026-07-21g: Stability + draft profile from live abilities

### Delivered
- **Benign sequence gaps** (≤15) map to Healthy — small GC relocate gaps no longer Degraded
- **Proactive cache refresh**: on sequence stagnation (~180 ms) try ±2 MB window only (no full-scan in proactive path — that stalled the loop)
- **MaxCandidates** default 32→64
- **GET `/api/abilities`** — full live ability inventory
- **POST `/api/profiles/draft-from-telemetry`** — disabled draft with real ability IDs, empty keys (no invented combat data)
- Status payload `abilitySummary` (count + sample)
- Live draft written: `profiles/draft-warrior-45-live.json` (55 abilities, Warrior L45, disabled)
- Suite **552/552**

### Operator next
1. Fill key bindings in `draft-warrior-45-live.json` for abilities you want to use (keys still empty)
2. Enable only after calibration; keep profile disabled until ready
3. Target a hostile NPC for readiness (friendly NPCs block arm)

## Session 2026-07-21f: Live Healthy + ClientVersion + Continuity

### Live evidence (2026-07-21, process `rift_x64` PID 31584, player Atank L45 warrior)
- `GET /api/status` unauthenticated **200** (token auth removed earlier; loopback-only)
- Provider **Healthy** with advancing sequence, `knownness` all known, **55 abilities**
- Scanner partial-select on `CandidateLimitExceeded` works (limit hits still common from Lua GC string copies)
- Occasional `ContinuityDegraded` from materialize throttle leaving sequence ahead of wire image — fixed in bridge 0.1.2 (always materialize with sequence)

### Delivered this session
- BotDsBridge **0.1.2**: `Inspect.System.Version()` is a **table** `{external,build,internal}` — publish `.external` (never `tostring(table)` which produced `table: 0x…`)
- Always materialize scannable string with each frame write (no deferred materialize)
- `ArmingReadinessService` reports live frame/player/provider even with no active profile
- Field matrix updated for Version table shape; suite **551/551**
- Deployed via `deploy-addon.cmd` to OneDrive AddOns path

### Operator action required
1. In RIFT: `/reloadui` (expect chat `BotDs Bridge v0.1.2` + corner label)
2. Confirm `/api/status` `clientVersion` is a real external/build string, not `table: 0x…`
3. Confirm Healthy is steady (few/no ContinuityDegraded flaps)

### Still deferred (no invented Warrior combat data)
- Real Warrior profile bindings/keys (disabled-warrior-45 stays placeholder)
- M8 live arm/dry-run soak, binding calibration, 30-min soak
- M9 packaging

## Session 2026-07-21d: M2 live telemetry code-complete

### Delivered
- `ITelemetrySource` + `SnapshotTelemetrySource` DI
- `TargetKnownness` + `GameInputReady` on `TelemetryFrame`; V5 flags bits 2–3; parser reserved mask bits 4–7
- Mapper known-empty vs unknown lists; KnownNoTarget vs Unknown vs KnownTarget
- BotDsBridge: known-no-target section emit; GameInputReady flags; `BotDsBridgeRegion` pin
- Dashboard status `knownness` object
- `M2LiveTelemetryTests` (7); full suite **544/544**
- Live probe: provider not Healthy (0 candidates) → blocked residual documented

## Session 2026-07-21c: M8 phase close-out (code-complete)

### Delivered
- `M8ClosedLoopGateTests`: Live gates (hotkey/bindings/collision), ack match vs non-match, timeout path (existing), pending-block, target/session invalidation, emergency-hotkey host stop wiring
- Full suite **537/537**
- Live probe: RIFT attached, provider not Healthy (0 V5 candidates) — blocked evidence retained for session; no invented Warrior fixture
- ROADMAP M8 marked **Code-complete** with deferred live exit list (not falsely Complete)

### Launch verification (loopback)
- `GET /api/coordinator`: `outputMode=Disabled`, emergency hotkey `Ctrl+Shift+F12` `registered=true`
- `GET /api/status`: scanner attached; provider Disconnected until addon publishes

## Session 2026-07-21b: Emergency Hotkey + External Action Conflict

### Delivered
- `IEmergencyHotkey` / `WindowsEmergencyHotkey` / `FakeEmergencyHotkey` with `VirtualKeyMap` parsing
- `EmergencyHotkeyHostedService` registers `Ctrl+Shift+F12` (configurable) at startup; triggers EmergencyStop + Disable
- Live mode requires registered emergency hotkey (`BotDs:Action:RequireEmergencyHotkeyForLive`, default true)
- Profile bindings that collide with the emergency hotkey block mode changes
- `ExternalActionConflictDetector`: unexplained ability cooldown while armed → `StopReason.ExternalActionConflict`
- Dashboard coordinator payload includes emergency hotkey registration status
- Tests: **527/527**

## Session 2026-07-21a: M8 Closed-Loop Code (Ack + Binding Verification)

### Delivered
- `ActionAcknowledgementMatcher` (Core): pure Cast/Cooldown/Resource/Aura matching against pre-dispatch baseline; session/target/source invalidation
- `BindingVerificationTracker` (Core): Unverified/Verified/Mismatch per ability alias; Live blockers for required bindings
- `ActionCoordinator`: captures baselines, observes pending every tick, records `Acknowledged` / `PendingInvalidated`, Live ack-timeout → `StopReason.ActionNotAcknowledged` + disable output; successful ack marks binding Verified; Live mode requires verified bindings (`BotDs:Action:RequireBindingVerificationForLive`, default true)
- `EvaluatorLoop`: always observes pending ack/timeout even when evaluation produces no action
- `ControllerStateMachine.Stop(StopReason, message)` for fail-closed non-estop stops
- Dashboard: `GET /api/bindings`, `POST /api/control/bindings`, coordinator payload includes binding states
- `run-botds.cmd` convenience launcher (repo root) + gitignored `botds.local.cmd` / `publish/`
- Tests: 522 passing (was 512)

### Live status at end of session
- RIFT `rift_x64` running; scanner attaches
- Provider Faulted / 0 valid V5 candidates until in-game `/reloadui` loads BotDsBridge
- Dashboard restart via `run-botds.cmd` after this session

## Session 2026-07-20c: M8 Code Completion + M9 Prep

### Commits

| Commit | What |
|--------|------|
| `434e49e` | Acknowledgement kind validation in profile loader (M8 per PLAN.md §8) — 2 new tests |
| (latest) | Dashboard 404 fix + AGENTS.md stale claims update |

### Changes

- **Dashboard 404 fix**: Added `app.MapFallbackToFile("index.html")` to Program.cs so root URL serves the dashboard
- **AGENTS.md updated**: Removed stale "not implemented" claims — TelemetryReaderLoop, V5ScannerService, ActionCoordinator, and WindowsKeySink are all complete
- **Win-x64 publish**: `dotnet publish src/BotDs.App/BotDs.App.csproj -c Release -r win-x64 --self-contained false -o publish/`

### Live Verification (Scanner + Dashboard)

- BotDsBridge addon installed to `%USERPROFILE%\OneDrive\Documents\RIFT\Interface\AddOns\BotDsBridge\` (shell MyDocuments; install-tree / non-OneDrive Documents alone are wrong on this machine)
- Scanner successfully attaches to RIFT PID 31584 (`isAttached: true`, metrics flowing)
- Provider status: Faulted — `/reloadui` needed in RIFT to load the addon and emit V5 frames
- App: `http://localhost:5068` — loopback API, **no token auth**
- Start: `run-botds.cmd` or `BotDs__Scanner__ProcessName=rift_x64 ASPNETCORE_URLS=http://localhost:5068 dotnet run --project src/BotDs.App`

### Test Count: 544/544

## Quick Start / Setup Guide

### Prerequisites
- .NET 10 SDK (`global.json` pins to `10.0.204`)
- Windows x64
- RIFT MMO installed

### 1. Restore and verify
```cmd
dotnet restore BotDs.sln
dotnet build BotDs.sln --no-restore
dotnet test BotDs.sln --no-restore
```
All 527 tests must pass, zero errors, zero warnings.

### 2. Install the addon
Copy `addons/BotDsBridge/BotDsBridge/` into RIFT's **user** addon directory.
Resolve the path from the shell known folder (often OneDrive-backed), not a
hard-coded non-redirected `Documents` path:
```cmd
mkdir "%USERPROFILE%\OneDrive\Documents\RIFT\Interface\AddOns\BotDsBridge"
copy addons\BotDsBridge\BotDsBridge\* "%USERPROFILE%\OneDrive\Documents\RIFT\Interface\AddOns\BotDsBridge\"
```
Or use `Environment.GetFolderPath(MyDocuments)\RIFT\Interface\AddOns\` in tools.
Verify siblings (JAB, ReaderBridge, etc.) exist in that folder before calling deploy done.
Glyph `Live\Interface\Addons` alone is not the client load path. In RIFT, `/reloadui`;
you should see "BotDs Bridge" top-left.

### 3. Launch the app
Preferred convenience wrapper from repo root:
```cmd
run-botds.cmd --open
```
Optional personal overrides: create gitignored `botds.local.cmd` with tokens/process name.

Manual equivalent:
```cmd
cd src\BotDs.App
set BotDs__Scanner__ProcessName=rift_x64
set ASPNETCORE_URLS=http://localhost:5068
dotnet run
```
Or with explicit PID:
```cmd
set BotDs__Scanner__ProcessId=12345
set BotDs__Scanner__ProcessName=rift_x64
dotnet run
```

### 4. Open the dashboard
Navigate to `http://localhost:5068` (no token).

### 5. Publish (standalone)
```cmd
dotnet publish src/BotDs.App/BotDs.App.csproj -c Release -r win-x64 --self-contained false -o publish/
```
Run the published app: `run-botds.cmd --publish` (exe: `publish\BotDs.App.exe`)

## Inputs Still Needed

The user has not supplied the current Warrior's actual:

- RIFT ability IDs and exact names.
- Action-bar keys.
- Intended ordered combat rules.
- Buff/debuff IDs used by the rotation.

Do not invent these values. The initial fixture should remain disabled until they are provided or observed through the finished Reader.

## Useful Commands

```text
deploy-addon.cmd
dotnet run --project src/BotDs.Tools -- paths
dotnet restore BotDs.sln
dotnet build BotDs.sln --no-restore
dotnet test BotDs.sln --no-restore
dotnet format BotDs.sln --verify-no-changes --no-restore
node --check src/BotDs.App/wwwroot/js/app.js
luac -p addons/BotDsBridge/BotDsBridge/main.lua
git diff --check
```
