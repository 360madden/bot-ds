# BotDs Project Knowledge

## RIFT addon path (durable — agents: never skip)

- **Doc:** `docs/rift-local-paths.md`
- **Code:** `BotDs.Core.RiftLocalPaths`
- **Deploy:** `deploy-addon.cmd`
- Player addons: `{Environment.SpecialFolder.MyDocuments}\RIFT\Interface\AddOns\`
- This machine: `C:\Users\mrkoo\OneDrive\Documents\RIFT\Interface\AddOns\`
- **Wrong:** non-OneDrive `Documents\RIFT\...`, Glyph `Live\Interface\Addons` as primary
- Deploy is valid only if parent AddOns already has siblings (JAB, ReaderBridge, …)

## What This Is

A personal, local combat-only bot for Gamigo's RIFT MMO on Windows x64. C# .NET 10 app that observes game state via a Lua addon → telemetry transport → memory reader, evaluates data-driven combat profiles, and sends foreground keyboard input via `SendInput`. Movement, pathfinding, navigation, target acquisition/switching are out of scope.

**Status:** See `HANDOFF.md` / `ROADMAP.md` (M2/M8 code-complete with live residuals; M8 offline safety-hardening complete). 600 tests green. All gates passing.

## Project Layout

```
BotDs.sln
├── src/
│   ├── BotDs.Core/          # net10.0 — domain models, profiles, evaluator (transport-neutral)
│   ├── BotDs.Reader/        # net10.0-windows — V5 memory scanner, Win32 interop (unsafe)
│   ├── BotDs.Input/         # net10.0-windows — key sink, SendInput, emergency hotkey
│   └── BotDs.App/           # net10.0-windows — ASP.NET Core dashboard, controller, services
├── tests/BotDs.Tests/       # net10.0-windows — xUnit (600 tests)
├── profiles/                # Versioned JSON combat profiles
├── schemas/                 # JSON Schema for combat profiles
├── addons/BotDsBridge/      # Lua addon v0.2.1 + PROTOCOL.md (V5 wire-format spec, schema v2)
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
RIFT Inspect APIs → BotDsBridge Lua addon (v0.2.1, schema v2) → V5 process memory
  → TelemetryReaderLoop → SnapshotAssembler → ObservationSnapshot
    ├→ CombatEvaluator → ActionDecision
    │    └→ ActionCoordinator → WindowsKeySink (SendInput, foreground-only)
    └→ Dashboard (SSE + REST, loopback only)
```

## Dashboard Auth

Dashboard API is **loopback-only with no token auth** (personal local tool). Non-loopback requests get 403.

## Profiles

Versioned JSON in `profiles/`. Schema at `schemas/combat-profile.schema.json`. C# runtime validation is authoritative. Key constraints:
- Must specify `character.calling`, ≥1 enabled binding, ≥1 enabled rule
- Must not specify `character.build` (V5 can't observe it; build identity via required ability set)
- Enabled rules cannot reference disabled bindings
- Level ranges must overlap between rules and profile range

## Action Coordinator

Three output modes: **Disabled** (startup default), **DryRun** (log-only, never calls sink, no acks, no binding verification), **Live** (requires: Live-capable sink with matching PID, verified bindings, registered emergency hotkey, known-ready game input, only Cast/Cooldown ack kinds). DryRun dispatches are log-only and do not create pending actions.

## Current Limitations

1. **Live mode unproven** — DryRun proof still needed against live RIFT client. All offline safety gates pass.
2. **Warrior fixture is disabled** — real ability IDs, keys, and rotation rules not yet supplied by user.
3. **Immutable Lua string** — V5 stable-memory contract relies on GC/allocator behavior; live-proven working but formal 10,000-publication soak not yet performed.
4. **No movement, pathfinding, or navigation** — explicitly out of scope.

## Milestone Roadmap

| Milestone | Status | What |
|-----------|--------|------|
| M0 | Complete | Foundation: Core, Reader, App, tests |
| M1 | Decided | Transport decision gate — V5 process memory selected |
| M2 | Code-complete | Live telemetry provider (live soak deferred) |
| M3 | Complete | Hosted source loop, snapshot assembly, replay |
| M4 | Complete | Dashboard settings, metrics, source views |
| M5 | Complete | Profiles: strict validation, editor, progression |
| M6 | Complete | Dry-run action coordinator (log-only) |
| M7 | Complete | Foreground Windows `SendInput` + emergency hotkey |
| M8 | Code-complete | Closed-loop live combat (offline hardening complete; live residual deferred) |
| M9 | Planned | Final acceptance, packaging, performance soak |
