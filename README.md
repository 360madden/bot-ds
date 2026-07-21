# BotDs: RIFT Combat Automation

Personal, local combat-only bot for Gamigo's RIFT MMO. The intended data path is a Lua addon, one current-client-validated local telemetry transport, an external C# provider, and data-driven combat profiles. The existing V5 process-memory Reader is the preferred candidate, subject to the M1 stable-storage gate. Movement, pathfinding, and navigation are out of scope.

Project direction is defined by the [formal implementation plan](PLAN.md) and [formal roadmap](ROADMAP.md).

**Source:** [github.com/360madden/bot-ds](https://github.com/360madden/bot-ds) · default branch **`main`**.

## Architecture

```
Lua addon (BotDsBridge)
  |  Observes structured RIFT state and publishes through the M1-selected transport
  v
External provider (BotDs.Reader)
  |  Current V5 candidate: process attach, sentinel scanning, CRC validation, metrics
  v
Core (BotDs.Core)
  |  TelemetryFrame, CombatProfile records, JSON loader/validator, CombatEvaluator
  v
App (BotDs.App)
  |  ASP.NET Core localhost dashboard (loopback-only, no token), SSE streaming,
  |  ControllerStateMachine, EvaluatorLoop, ProfileService, NDJSON structured logs
```

### Project tree

```
BotDs.sln
|-- src/
|   |-- BotDs.Core/          # net10.0: domain models, profiles, evaluator
|   |-- BotDs.Reader/        # net10.0-windows: V5 scanner and Win32 memory reader
|   `-- BotDs.App/           # net10.0-windows: dashboard, controller, profiles, evaluator loop
|-- tests/BotDs.Tests/       # net10.0-windows: xUnit coverage
|-- profiles/                # Versioned JSON combat profiles
|-- schemas/                 # JSON Schema for combat profiles
|-- addons/BotDsBridge/      # Lua addon and V5 protocol specification
`-- context/                 # Research and architecture evidence
```

## Quick start

Requires .NET 10 SDK (pinned to `10.0.204` in `global.json`; `latestPatch` roll-forward).
Node.js is required only for the dashboard JavaScript syntax gate, and `luac` is required for the addon syntax gate.

```text
dotnet restore BotDs.sln
dotnet build BotDs.sln --no-restore
dotnet test BotDs.sln --no-restore
dotnet format BotDs.sln --verify-no-changes --no-restore
node --check src/BotDs.App/wwwroot/js/app.js
luac -p addons/BotDsBridge/BotDsBridge/main.lua
git diff --check
```

## Run

```text
run-botds.cmd                 # development (dotnet run)
run-botds.cmd --publish       # after: dotnet publish ... -o publish/
deploy-addon.cmd              # BotDsBridge → shell MyDocuments AddOns
```

See `docs/first-run.md` for the full first-run and live DryRun checklist.

## Dashboard access

No API token. The dashboard and `/api/*` endpoints are **loopback-only** (`localhost` / `127.0.0.1`). Open `http://localhost:5068` after starting the app — no login step.

## Profiles and schema

Combat profiles are versioned JSON files placed in `profiles/` (configurable via `BotDs:Profiles:Directory`). See `profiles/README.md` and the JSON Schema at `schemas/combat-profile.schema.json`. An enabled profile must specify `character.calling`, at least one enabled ability binding, at least one enabled rule, and must not specify `character.build` (V5 does not observe build identity). Disabled profiles still receive base structural validation but skip enabled-profile-only executability checks.

## Addon install

**Addon deploy (durable):** see `docs/rift-local-paths.md`. Use `deploy-addon.cmd` (C# resolves shell MyDocuments). On this machine: `C:\Users\mrkoo\OneDrive\Documents\RIFT\Interface\AddOns\BotDsBridge\`. Never use Glyph `Live\Interface\Addons` or a non-shell Documents path as primary.

## Scanner selector

The Reader locates the target process by PID, name, or both via `ProcessSelector`. Configure it in `V5ScannerService` constructor parameters. On each read cycle the scanner validates the cached sentinel address, falls back to a full V5 region scan (with stale backoff to avoid busy loops), performs a stable dual-buffer read with CRC validation, and returns a `ScannerReadResult` with transport health and metrics.

## Current limitations

- **Live residual**: M2/M8 are code-complete; full soak, failure matrix, and operator key calibration remain environment-gated (see `ROADMAP.md` / `HANDOFF.md`).
- **Lua immutable region**: each materialize creates a new process-memory string (GC copies); scanner uses cache + ranking + dual schema. No movement/pathfinding.
- **Keys not observed**: action bar yields ability ids only; profile keys are user-confirmed (draft may suggest defaults from slot index).
- **No movement, pathfinding, or navigation**: explicitly out of scope.

## Privacy

Default launch binds loopback; API middleware rejects non-loopback callers. The application does not collect credentials or chat content. Logs are local structured NDJSON. No dashboard tokens.
