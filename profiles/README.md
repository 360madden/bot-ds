# Combat Profiles

Versioned JSON combat profiles for the BotDs combat bot.

## Format

Each profile is a JSON file conforming to `../schemas/combat-profile.schema.json`. Top-level fields:

| Field | Type | Notes |
|---|---|---|
| `$schema` | string | Optional; points to the schema for editor validation. |
| `profileVersion` | int | Must be `1`. |
| `enabled` | bool | Default `true`. Disabled profiles are never armed by the evaluator. |
| `id` | string | Unique profile identifier. |
| `character` | object | Calling (class), optional level bounds, optional build. |
| `abilities` | map | Alias to binding (abilityId, key, enabled, required, level bounds). |
| `rules` | array | Ordered combat rules evaluated in sequence; first match fires. |

## Enabled vs Disabled Profiles

- **Enabled** profiles (`"enabled": true` or omitted): the evaluator will attempt to arm them. Validation requires at least one enabled rule referencing an enabled ability binding that overlaps the profile level range. Enabled profiles must not specify `character.build`.
- **Disabled** profiles (`"enabled": false`): the evaluator stops immediately with `ProfileMismatch`. Disabled profiles may retain a `build` value for reference, but they are never evaluated.

## Ability Bindings

Each binding maps a short alias (used in rules) to a game ability:

- `abilityId`: RIFT ability identifier (must be non-empty when binding is enabled).
- `key`: keyboard key (must be non-empty when binding is enabled).
- `enabled`: whether this binding is active.
- `required`: when `true` and the binding's level range includes the player, the ability must be present in telemetry. If the ability inventory is unknown, evaluation stops.
- `minimumLevel` / `maximumLevel`: inclusive level bounds; when omitted, no level restriction applies.

### Required Binding Semantics

Required bindings are checked only when their level range includes the player's current level. The bounds are inclusive: a binding with `minimumLevel: 10, maximumLevel: 20` applies to levels 10 through 20. If any applicable required binding's ability is missing from telemetry, evaluation stops.

## Combat Rules

Each rule references an ability alias and declares conditions in `when`. Rules are evaluated top-to-bottom; the first rule whose conditions all pass fires. Conditions include:

- Target hostility, player/target combat state, target casting/interruptibility.
- Ability usability, range, cooldown readiness.
- Player/target health thresholds (percentage ranges).
- Resource minimum.
- Required/forbidden player/target auras (by id).

## Disabled Warrior Fixture

`disabled-warrior-45.json` is a placeholder profile for a level-45 Warrior. It is **disabled** and contains only a placeholder binding with empty abilityId/key. It demonstrates the profile format without being arming-capable. Do not treat it as a working combat profile.

## Verification

```bash
dotnet build BotDs.sln --no-restore
dotnet test tests/BotDs.Tests/BotDs.Tests.csproj --no-restore --filter "FullyQualifiedName~ProfileServiceTests"
```
