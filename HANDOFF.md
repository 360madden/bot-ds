# Implementation Handoff

Last updated: 2026-07-19

## Resume Instruction

Restart OpenCode from `C:\work\bot-ds`, then ask: `resume implementation with configured swarm`.

Read these files before changing code:

1. `AGENTS.md`
2. `context/README.md` and every indexed context document
3. This handoff

## Agent Runtime Status

An earlier five-agent swarm exposed an OpenCode runtime/database schema mismatch. The process used OpenCode `1.15.3`, while the shared SQLite schema required `session_message.seq`.

The database passed `PRAGMA integrity_check`, and the global package was upgraded to OpenCode `1.18.3`. Subsequent parallel agent, integration, and review sessions completed successfully. Restart OpenCode after changing project agent configuration because configuration is loaded only at process startup.

Related upstream report: <https://github.com/anomalyco/opencode/issues/31204>

## Approved Product Plan

The user approved implementation of a Windows x64 combat-only RIFT bot with:

- C# and .NET 10 for the application and all new executable helpers.
- A Lua addon component that publishes structured game state for the external Reader subsystem.
- A high-performance, low-latency Reader with an integrity-checked, double-buffered protocol.
- Current player-selected hostile target only; no target acquisition or switching.
- Versioned JSON combat profiles and no Warrior-specific engine logic.
- A level-45 Warrior only as the first data fixture.
- Foreground RIFT action output through configured key bindings.
- A localhost ASP.NET Core dashboard with live state and full local controls.
- Minimal structured local logs, monotonic durations, useful error boundaries, and enough action history to diagnose personal use without collecting credentials or chat content.
- Personal-tool operation: publisher policy, terms-of-service interpretation, account enforcement, and account-risk acceptance are explicitly not implementation gates or arming confirmations.
- Privacy remains a requirement: loopback-only control, no credential capture, no chat-content collection, and no unnecessary sensitive identifiers in logs.
- Movement, pathfinding, and navigation remaining out of scope.

The detailed reviewed plan is preserved in the conversation history. The context documents record source evidence and unresolved runtime validation requirements.

## Repository Constraints

- All new executable helpers, utilities, generators, and maintenance tools must be C# targeting .NET 10 or newer.
- Do not add Python helpers or scripts.
- Do not add standalone PowerShell applications or `.ps1` files.
- `.cmd` files may be thin convenience wrappers; application logic belongs in C#.
- Preserve unrelated user or agent changes.
- Do not commit or push unless explicitly requested.

### Personal Tool Workflow

- Optimize for one local operator, direct functionality, and straightforward maintenance rather than commercial-product operations.
- Do not spend implementation or review cycles evaluating publisher policy or account enforcement unless the user explicitly asks for that research.
- Do not add terms/account-risk warnings or acknowledgements to the dashboard or runtime controls.
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

### Partial Core Implementation

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

Result: zero warnings, zero errors, clean formatting, and 192 passing tests.

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
- Dashboard arming confirms only current technical readiness; it does not request terms or account-risk acknowledgement.
- Profile reload is atomic, rejects duplicate IDs, and clears stale profiles when the configured directory disappears.
- The solution currently has 316 passing Core, protocol, scanner/native, controller, security, profile, and telemetry lifecycle tests.

Verification completed on 2026-07-19:

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

1. Resolve the live addon backing-storage invariant: the current immutable Lua string is rebuilt repeatedly, so address stability and stale-copy behavior require runtime validation or a transport redesign before enabling live publication.
2. Populate live addon unit, ability, cast, and aura sections after current-client conformance validation.
3. Add a hosted Reader service that publishes normalized snapshots and preserves the last full snapshot across heartbeat-only frames without carrying state across inspection failure, session change, or transport faults.
4. Implement action acknowledgement tracking, rate limits, foreground/focus checks, and emergency-stop input hooks before any keyboard actuator is added.
5. Add a minimal local action history and useful crash diagnostics without separate production-style audit pipelines or long retention.
6. Add endpoint-level dashboard integration tests and recorded replay tests; scanner/native fixtures, malformed protocol, middleware security, profile reload, and controller lifecycle coverage now exist.
7. Add Reader attribution and product/setup documentation, including dashboard token and scanner selector configuration.
8. Continue findings-only review after each implementation slice; the scanner review was completed and its actionable findings were resolved.

## Inputs Still Needed

The user has not supplied the current Warrior's actual:

- RIFT ability IDs and exact names.
- Action-bar keys.
- Build/soul label.
- Intended ordered combat rules.
- Buff/debuff IDs used by the rotation.

Do not invent these values. The initial fixture should remain disabled until they are provided or observed through the finished Reader.

## Useful Commands

```text
opencode --version
opencode debug config
git status --short --branch
dotnet restore BotDs.sln
dotnet build BotDs.sln --no-restore
dotnet test BotDs.sln --no-build
```
