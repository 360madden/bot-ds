# BotDs Project Knowledge

## What This Is

A personal, local combat-only bot for Gamigo's RIFT MMO on Windows x64. C# .NET 10 app that observes game state via a Lua addon → telemetry transport → memory reader, evaluates data-driven combat profiles, and sends foreground keyboard input via `SendInput`. Movement, pathfinding, navigation, target acquisition/switching are out of scope.

**Status:** M0 (foundation) complete — 378 tests pass. M1 (transport decision gate) is active. No live observation or action output exists yet.

## Project Layout

```
BotDs.sln
├── src/
│   ├── BotDs.Core/          # net10.0 — domain models, profiles, evaluator (transport-neutral)
│   ├── BotDs.Reader/        # net10.0-windows — V5 memory scanner, Win32 interop (unsafe)
│   └── BotDs.App/           # net10.0-windows — ASP.NET Core dashboard, controller, services
├── tests/BotDs.Tests/       # net10.0-windows — xUnit (378 tests)
├── profiles/                # Versioned JSON combat profiles
├── schemas/                 # JSON Schema for combat profiles
├── addons/BotDsBridge/      # Lua addon + PROTOCOL.md (V5 wire-format spec)
└── context/                 # Historical RIFT automation research (read-only reference)
```

## Commands

All gates must pass before committing:

```bash
dotnet restore BotDs.sln
dotnet build BotDs.sln --no-restore
dotnet test BotDs.sln --no-restore
dotnet format BotDs.sln --verify-no-changes --no-restore
node --check src/BotDs.App/wwwroot/js/app.js
luac -p addons/BotDsBridge/BotDsBridge/main.lua
git diff --check
```

## Key Conventions

- **.NET 10 SDK** pinned to `10.0.204` via `global.json` (`latestPatch` roll-forward)
- **Build.props:** `LangVersion=latest`, `TreatWarningsAsErrors=true`, `AnalysisLevel=latest`, deterministic
- **C# only** — no Python, no standalone PowerShell/.ps1 files. `.cmd` files are thin wrappers only.
- **No hard-coded Warrior logic** — the engine must remain calling/character-agnostic. Profiles are versioned JSON.
- **Fail closed** — unknown≠empty, stale state blocks action, missing telemetry stops evaluation.
- **Privacy:** loopback-only control, no credential/chat capture, NDJSON logs with 14-day retention.
- **No auto-arm** — action output never re-arms after stop/fault/session change.
- **One action in flight** — no duplicate unacknowledged dispatches.

## Architecture Data Flow

```
RIFT Inspect APIs → BotDsBridge Lua addon → Transport (M1 gate) → ITelemetrySource
  → HeartbeatSnapshotAssembler → ObservationSnapshot
    ├→ CombatEvaluator → ActionIntent
    │    └→ ActionCoordinator → ForegroundProcessGuard → WindowsKeySink (SendInput)
    └→ Dashboard (SSE + REST, loopback only)
```

## Dashboard Auth

Tokens set via env vars or user secrets under `BotDs:Dashboard:ApiToken` and `BotDs:Dashboard:ControlToken`. Control token grants read+mutation. Empty tokens fail closed. Tokens sent as `Authorization: Bearer <token>`; control endpoints also accept `X-Control-Token`. All API restricted to loopback.

## Profiles

Versioned JSON in `profiles/`. Schema at `schemas/combat-profile.schema.json`. C# runtime validation is authoritative. Key constraints:
- Must specify `character.calling`, ≥1 enabled binding, ≥1 enabled rule
- Must not specify `character.build` (V5 can't observe it; build identity via required ability set)
- Enabled rules cannot reference disabled bindings
- Level ranges must overlap between rules and profile range

## Current Limitations (Gotchas)

1. **No hosted Reader loop** — scanner is testable but not wired as a hosted service. App publishes empty `TelemetryFrame`.
2. **Lua provider-only** — addon emits V5 envelope/heartbeat but stubs all game-state sections (Player, Target, Abilities, Auras). Immutable Lua string issue means V5 stable-memory contract is unproven.
3. **No action output** — no keyboard actuator exists. Evaluator produces `ActionDecision` records but they're logged only.
4. **Warrior fixture is disabled** — real ability IDs, keys, and rotation rules not yet supplied by user.
5. **M1 transport unresolved** — either V5 process memory (needs mutable storage proof) or optical addon-rendered fallback.

## Milestone Roadmap

| Milestone | Status | What |
|-----------|--------|------|
| M0 | Complete | Foundation: Core, Reader, App, tests (378 passing) |
| M1 | Active | Transport decision gate + current-client conformance |
| M2 | Planned | Live telemetry provider (Player + Target + Abilities + Auras) |
| M3 | Planned | Hosted source loop, snapshot assembly, replay |
| M4 | Planned | Dashboard settings, metrics, source views |
| M5 | Planned | Profiles: strict validation, editor, Warrior fixture, generic profile |
| M6 | Planned | Dry-run action coordinator (no native input) |
| M7 | Planned | Foreground Windows `SendInput` + emergency hotkey |
| M8 | Planned | Closed-loop live combat (incremental enablement) |
| M9 | Planned | Final acceptance, packaging, performance soak |

## OpenCode Agent Swarm

Project uses `.opencode/agents/` with path-based ownership:
- `protocol-engineer`: Reader + addon transport
- `dashboard-worker`: wwwroot frontend
- `general`: App host/services (excluding wwwroot)
- `test-worker`: tests
- `core-worker`: Core + profiles + schemas
- `integrator`: cross-project integration, DI wiring, build
- `reviewer`: final findings-only review
- `architect`, `researcher`: read-only design/research

GPT models reserved for architecture/integration/review. Non-GPT agents use OpenCode Go or catalog-free models.
