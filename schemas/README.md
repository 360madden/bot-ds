# Schemas

JSON Schema for BotDs combat profiles.

## Location

`combat-profile.schema.json` is the canonical schema for profile files in `../profiles/`.

## Schema Version

Draft 2020-12. Profile `$id`: `https://botds.local/schemas/combat-profile.json`.

## Key Constraints

### Top Level

- `profileVersion` must be `1`.
- `id` is required, non-empty.
- `character`, `abilities`, `rules` are required.
- `abilities` must have at least one entry.
- `rules` must have at least one entry.

### Character

- `calling` is required, non-empty.
- `minimumLevel` / `maximumLevel`: positive integers with inclusive bounds. When both are present, minimum cannot exceed maximum.
- `build`: optional string. Schema enforces that **enabled profiles must not specify build** via an `allOf` conditional (if `enabled` is not `false`, then `character` must not contain `build`).

### Abilities

Each entry requires `abilityId` and `key`. Optional: `enabled` (default `true`), `required` (default `true`), `minimumLevel`, `maximumLevel`.

### Rules

Each entry requires `id` and `ability`. Optional: `enabled` (default `true`), `acknowledgement` (default `"cooldown"`), `when` (conditions object). The `ability` field must reference a key in the `abilities` map.

### Conditions (`when`)

- Boolean fields: `targetHostile` (default `true`), `targetIsPlayer`, `playerInCombat`, `targetInCombat`, `abilityUsable` (default `true`), `abilityInRange` (default `true`), `cooldownReady` (default `true`), `targetCasting`, `targetCastInterruptible`.
- Health thresholds: `playerHealthBelowPercent`, `playerHealthAbovePercent`, `targetHealthBelowPercent`, `targetHealthAbovePercent` (0–100).
- Resource: `resourceAtLeast` (zero or greater).
- Auras: `requiredPlayerAuras`, `forbiddenPlayerAuras`, `requiredTargetAuras`, `forbiddenTargetAuras` (arrays of aura id strings).

## Runtime Cross-Field Validation

The JSON Schema covers structural shape. Additional cross-field rules are enforced at load time by `CombatProfileLoader.Validate()` in `BotDs.Core`:

- Enabled bindings must have non-empty `abilityId` and `key`.
- Rule `ability` aliases must exist in the `abilities` map.
- Level bounds must be positive and not inverted.
- Enabled profiles must have at least one enabled rule referencing an enabled binding that overlaps the profile level range.
- Enabled profiles must not specify `character.build`.
- Aura id arrays must contain non-empty strings.

These runtime checks are not expressible in JSON Schema alone.

## Verification

```bash
dotnet build BotDs.sln --no-restore
dotnet test tests/BotDs.Tests/BotDs.Tests.csproj --no-restore --filter "FullyQualifiedName~ProfileServiceTests"
```
