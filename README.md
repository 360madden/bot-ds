# BotDs: RIFT Combat Automation

Personal, local combat-only bot for Gamigo's RIFT MMO. The intended data path is a Lua addon, one current-client-validated local telemetry transport, an external C# provider, and data-driven combat profiles. The existing V5 process-memory Reader is the preferred candidate, subject to the M1 stable-storage gate. Movement, pathfinding, and navigation are out of scope.

Project direction is defined by the [formal implementation plan](PLAN.md) and [formal roadmap](ROADMAP.md).

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
  |  ASP.NET Core localhost dashboard (auth via Bearer tokens), SSE streaming,
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

## Dashboard token setup

Set `BotDs:Dashboard:ApiToken` and `BotDs:Dashboard:ControlToken` through environment variables or .NET user secrets. Empty tokens disable their respective read/control credentials. A valid control token also grants read access. The dashboard sends Bearer tokens via the `Authorization` header; control endpoints also accept `X-Control-Token`. All API endpoints are restricted to loopback addresses.

```json
{
  "BotDs": {
    "Dashboard": {
      "ApiToken": "your-read-token",
      "ControlToken": "your-control-token"
    }
  }
}
```

## Profiles and schema

Combat profiles are versioned JSON files placed in `profiles/` (configurable via `BotDs:Profiles:Directory`). See `profiles/README.md` and the JSON Schema at `schemas/combat-profile.schema.json`. An enabled profile must specify `character.calling`, at least one enabled ability binding, at least one enabled rule, and must not specify `character.build` (V5 does not observe build identity). Disabled profiles still receive base structural validation but skip enabled-profile-only executability checks.

## Addon install

Copy the `addons/BotDsBridge/BotDsBridge` folder into `<RIFT install>\Interface\Addons\`. The addon is a provider-only skeleton: it emits the V5 envelope and heartbeat but stubs all game-state sections (Player, Target, Abilities, Auras).

## Scanner selector

The Reader locates the target process by PID, name, or both via `ProcessSelector`. Configure it in `V5ScannerService` constructor parameters. On each read cycle the scanner validates the cached sentinel address, falls back to a full V5 region scan (with stale backoff to avoid busy loops), performs a stable dual-buffer read with CRC validation, and returns a `ScannerReadResult` with transport health and metrics.

## Current limitations

- **No hosted Reader loop**: the scanner is instantiable and testable but not wired as a hosted service. The application currently publishes an empty `TelemetryFrame`.
- **Lua provider-only/stable-address issue**: the addon emits only the V5 envelope/heartbeat. Game-state sections are stubbed. The immutable Lua string is rebuilt repeatedly during each frame, so stable-address, in-place publication, and allocation-cost requirements need a transport redesign or validated client storage facility before live publication.
- **No action output**: no keyboard actuator or foreground injection exists. The evaluator produces `ActionDecision` records but they are logged only.
- **No movement, pathfinding, or navigation**: explicitly out of scope.

## Privacy

Default launch profiles bind localhost, and API middleware rejects non-loopback callers. Explicit listener-level loopback enforcement is scheduled in roadmap M3. The application does not collect credentials, chat content, or unnecessary sensitive identifiers. Logs are structured NDJSON with 14-day retention. Dashboard tokens are never placed in URLs; SSE uses authenticated `fetch()` streaming.
