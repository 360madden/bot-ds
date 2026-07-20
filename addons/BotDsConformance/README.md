# BotDsConformance — M1 API Surface Probe

Non-actuating RIFT addon that inspects every relevant addon API and dumps structured results to the RIFT client log.

## Install

Copy `BotDsConformance` folder into `<RIFT>\Interface\Addons\`.

## Usage

1. Launch RIFT and log in with the addon installed
2. Type `/reloadui` in chat to trigger a fresh inspection
3. Find the RIFT client log file (typically `%APPDATA%\RIFT\rift.log` or in the RIFT install directory)
4. Search for `[BotDsConformance]` to find the probe output
5. Copy the output block between `=== BotDsConformance M1 Probe ===` and `=== End Conformance Probe ===`

## What It Inspects

- `Inspect.Time.Frame()` — frame time
- `Inspect.System.Version()` — client build/version
- `Inspect.System.Secure()` — secure mode status
- `Inspect.Unit.Detail("player")` — full player state with all fields
- `Inspect.Unit.Castbar("player")` — player castbar
- `Inspect.Unit.Detail("player.target")` — target state with all fields
- `Inspect.Unit.Castbar("player.target")` — target castbar
- `Inspect.Ability.New.List()` + `Inspect.Ability.New.Detail()` — abilities (first 5 sampled)
- `Inspect.Buff.List("player")` + `Inspect.Buff.Detail()` — player buffs (first 3 sampled)
- `Inspect.Buff.List("player.target")` + `Inspect.Buff.Detail()` — target buffs (first 3 sampled)
- `Action.Bar.Page.Get()` — current action bar page
- `Action.Get(slot)` — action bar slots 1-12
- `Inspect.Unit.List()` — visible unit count
