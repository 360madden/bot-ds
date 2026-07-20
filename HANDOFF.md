# Implementation Handoff

Last updated: 2026-07-20

## Resume Instruction

Restart OpenCode from `C:\work\bot-ds`, then ask: `resume implementation with configured swarm`.

Read these files before changing code:

1. `AGENTS.md`
2. `PLAN.md`
3. `ROADMAP.md`
4. `context/README.md` and every indexed context document
5. This handoff

## Agent Runtime Status

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

The following completed successfully after restoring packages:

```text
dotnet restore BotDs.sln
dotnet build BotDs.sln --no-restore
dotnet test BotDs.sln --no-build
dotnet format BotDs.sln --verify-no-changes --no-restore
```

Result: zero warnings, zero errors, clean formatting, and 378 passing tests.

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
- The solution currently has 378 passing Core, protocol, scanner/native, controller, security, profile, and telemetry lifecycle tests.

Verification completed on 2026-07-20:

```text
dotnet build BotDs.sln --no-restore
dotnet test BotDs.sln --no-restore
dotnet format BotDs.sln --verify-no-changes --no-restore
luac -p addons/BotDsBridge/BotDsBridge/main.lua
```

The authenticated localhost smoke test returned `401` without a token, loaded the disabled fixture, and rejected arming that fixture.

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

Do not let concurrent agents edit the same project file. Let the primary agent perform package/project-file changes. After workers finish, use `integrator` for the full build and `reviewer` for findings-only final review.

## Remaining Implementation

The authoritative sequence and exit criteria are in `ROADMAP.md`. The active milestone is M1: transport and current-client conformance.

## Inputs Still Needed

The user has not supplied the current Warrior's actual:

- RIFT ability IDs and exact names.
- Action-bar keys.
- Intended ordered combat rules.
- Buff/debuff IDs used by the rotation.

Do not invent these values. The initial fixture should remain disabled until they are provided or observed through the finished Reader.

## Useful Commands

```text
dotnet restore BotDs.sln
dotnet build BotDs.sln --no-restore
dotnet test BotDs.sln --no-restore
dotnet format BotDs.sln --verify-no-changes --no-restore
node --check src/BotDs.App/wwwroot/js/app.js
luac -p addons/BotDsBridge/BotDsBridge/main.lua
git diff --check
```
