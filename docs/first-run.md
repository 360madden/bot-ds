# BotDs first-run (local)

## Prerequisites

- .NET 10 SDK (`global.json` pins `10.0.204`)
- RIFT client installed; shell **MyDocuments** RIFT AddOns tree (OneDrive on this machine — see `docs/rift-local-paths.md`)
- Git clone default branch is **`main`** (`https://github.com/360madden/bot-ds`)

## Verify offline

```text
dotnet restore BotDs.sln
dotnet build BotDs.sln --no-restore
dotnet test BotDs.sln --no-restore
```

Optional: `node --check src/BotDs.App/wwwroot/js/app.js`

## Run (development)

```text
run-botds.cmd
```

Dashboard: `http://localhost:5068` (loopback only, no token).

## Run (published)

```text
dotnet publish src/BotDs.App/BotDs.App.csproj -c Release -r win-x64 --self-contained false -o publish/
run-botds.cmd --publish
```

Executable: `publish\BotDs.App.exe` (not `BotDs.exe`).

## Addon

```text
deploy-addon.cmd
```

In RIFT: `/reloadui` — expect chat `BotDs Bridge v0.2.0` (or newer).

## Live DryRun checklist

1. Status Healthy; abilities inventory known (dashboard Abilities card).
2. Action bar populated when schema v2 is live.
3. Draft profile from live (button or `POST /api/profiles/draft-from-telemetry`).
4. **Confirm** any suggested keys in-game; enable only after confirm.
5. Hostile target → select profile → output **DryRun** → Disarmed until ready.
6. Live mode only after binding verification + emergency hotkey (fail-closed).

## Profiles

- Place JSON under `profiles/` (or `BotDs:Profiles:Directory`).
- `*.names.json` sidecars are authoring-only and are not loaded as combat profiles.
- Do not invent ability IDs or combat keys offline; use observed IDs only.
