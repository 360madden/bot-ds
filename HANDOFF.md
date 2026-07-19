# Implementation Handoff

Last updated: 2026-07-19

## Resume Instruction

Restart OpenCode from `C:\work\bot-ds`, then ask: `resume implementation with configured swarm`.

Read these files before changing code:

1. `AGENTS.md`
2. `context/README.md` and every indexed context document
3. This handoff

## Why A Restart Is Required

The attempted five-agent swarm exposed an OpenCode runtime/database schema mismatch. The running process used OpenCode `1.15.3`, while the shared SQLite schema requires `session_message.seq`. Five child sessions were created, but all failed before receiving their first prompt.

The database passed `PRAGMA integrity_check`. The global npm package was upgraded successfully to OpenCode `1.18.3`, which matches the newer schema behavior. The currently running OpenCode process still has the old runtime loaded, so subagents must not be retried until after restart.

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
- Strong structured logs, UTC/local timestamps, monotonic durations, correlation IDs, rotation, audit logs, crash envelopes, and error boundaries.
- Explicit user acceptance of current gamigo account risk.
- Movement, pathfinding, and navigation remaining out of scope.

The detailed reviewed plan is preserved in the conversation history. The context documents record source evidence and unresolved runtime validation requirements.

## Repository Constraints

- All new executable helpers, utilities, generators, and maintenance tools must be C# targeting .NET 10 or newer.
- Do not add Python helpers or scripts.
- Do not add standalone PowerShell applications or `.ps1` files.
- `.cmd` files may be thin convenience wrappers; application logic belongs in C#.
- Preserve unrelated user or agent changes.
- Do not commit or push unless explicitly requested.

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

This code is an initial implementation and has not received swarm review or test coverage yet.

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
```

Result: zero warnings and zero errors.

There are currently no substantive tests. Run `dotnet test BotDs.sln --no-build` after adding the first test files.

## Configured OpenCode Agents

No configured agent has been launched yet. Project-local definitions are under `.opencode/agents/`.

| Agent | Model | Intended Role |
| --- | --- | --- |
| built-in `general` | `opencode-go/deepseek-v4-flash`, high | Fast general implementation |
| built-in `explore` | `opencode/north-mini-code-free`, high | Free codebase exploration |
| `architect` | `opencode/gpt-5.6-sol`, xhigh | Read-only architecture decisions |
| `integrator` | `opencode/gpt-5.6-sol`, high | Cross-project integration |
| `protocol-engineer` | `opencode-go/deepseek-v4-pro`, max | Reader, protocol, native interop, and Lua contract |
| `reviewer` | `opencode/gpt-5.6-sol`, high | Read-only final review |
| `core-worker` | `opencode/north-mini-code-free`, high | Free isolated C# implementation |
| `test-worker` | `opencode/north-mini-code-free`, high | Free adversarial tests |
| `dashboard-worker` | `opencode/mimo-v2.5-free` | Free dashboard implementation |
| `researcher` | `opencode/nemotron-3-ultra-free` | Free read-only research |

Model-routing requirement:

- Higher-reasoning OpenCode agents use only OpenCode's GPT models or OpenCode Go models.
- Other OpenCode agents use models whose catalog cost is explicitly zero.
- This routing rule does not apply to external harnesses such as Codex, Freebuff, or Grok Build.

The merged configuration and agent definitions passed `opencode debug config` and `opencode debug agent ...` validation. Agent configuration is loaded only at process startup.

## Recommended Swarm After Restart

Launch independent work concurrently with strict path ownership:

| Agent | Ownership |
| --- | --- |
| `protocol-engineer` | `src/BotDs.Reader/**` and `addons/BotDsBridge/**` |
| `dashboard-worker` | `src/BotDs.App/wwwroot/**` |
| `general` | App host/services under `src/BotDs.App/**`, excluding `wwwroot` |
| `test-worker` | `tests/BotDs.Tests/**` |
| `core-worker` | Core review/fixes plus `profiles/**` and `schemas/**` when explicitly assigned |

Do not let concurrent agents edit the same project file. Let the primary agent perform package/project-file changes. After workers finish, use `integrator` for the full build and `reviewer` for findings-only final review.

## Remaining Implementation

1. Review and test the partial Core contracts.
2. Define one authoritative v5 protocol specification shared by C# and Lua.
3. Implement CRC, parser, process attachment, memory abstraction, stable/near/full scanning, candidate synchronization, health states, and metrics.
4. Implement the addon emitter, events, reconciliation, heartbeat, ability/cast/aura state, diagnostics, and protocol health.
5. Implement application orchestration, profile lifecycle, evaluator loop, acknowledgement tracking, arming, rate limits, and emergency stop.
6. Implement Serilog bootstrap/final configuration, JSONL application/action/audit logs, correlation, rotation, crash reporting, and dashboard log streaming.
7. Implement the localhost dashboard, authorization token, strict origin/host validation, SSE, profile controls, run controls, and responsive static UI.
8. Add profile JSON Schema and a disabled placeholder Warrior fixture until real ability IDs and bindings are supplied.
9. Add Reader attribution and product/setup documentation.
10. Add unit, protocol, scanner, evaluator, logging, dashboard, lifecycle, and replay tests.
11. Run format, restore, build, test, and findings-only review.

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
