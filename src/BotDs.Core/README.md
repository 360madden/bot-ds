# BotDs.Core

Core state models and combat evaluation logic for the BotDs combat bot.

## Scope

This library defines the data contracts shared between the Reader (game telemetry) and the combat evaluator. It does not read game memory, send input, or host services.

## State Models (`StateModels.cs`)

| Type | Purpose |
|---|---|
| `ProviderStatus` | Reader health, sequence number, age. `IsUsable(maxAge)` checks health == Healthy, not truncated, and age within bound. |
| `UnitState` | Player or target snapshot: health, resource, casting, level, calling, relation. `IsHostile` compares `Relation` case-insensitively. |
| `AbilityState` | Per-ability snapshot: availability, cooldown, usability, range, costs. `IsReady` requires `Available && CooldownRemaining == 0`. |
| `AuraState` | Buff/debuff on player or target: id, stacks, remaining time, is-debuff flag. |
| `TelemetryFrame` | Full frame: provider, player, target, ability map, player/target aura lists. Three `Is*Known` flags gate aura/ability checks. `TelemetryFrame.Empty(now)` returns a disconnected frame. |

### Completeness Flags

- `IsAbilitiesKnown`: false until the Reader populates the ability map. Required-ability checks stop with `ProviderUnavailable` when false.
- `IsPlayerAurasKnown` / `IsTargetAurasKnown`: false until the Reader populates aura lists. Aura condition checks reject when false.

## Combat Evaluator (`CombatEvaluator.cs`)

`CombatEvaluator.Evaluate(profile, frame)` returns an `EvaluationResult` with one of:

| State | Meaning |
|---|---|
| `Stopped` | Fatal condition; includes `StopReason` and message. |
| `WaitingForTarget` | No live hostile target selected. |
| `Evaluating` | No rule matched this frame; loop continues. |
| `Armed` | First matching rule fired; `ActionDecision` is populated. |

### Evaluation Order

1. Null profile -> `Stopped(IntegrityFailure)`; disabled profile -> `Stopped(ProfileMismatch)`.
2. Null frame -> `Stopped(IntegrityFailure)`; missing or stale provider -> `Stopped(TelemetryStale)`.
3. Player unavailable or dead -> `Stopped(PlayerUnavailable)` / `Stopped(PlayerDead)`.
4. Character mismatch (calling, level range) -> `Stopped(ProfileMismatch)`.
5. Build present on enabled profile -> `Stopped(ProfileMismatch)`.
6. Required bindings present and available; if inventory unknown and required bindings exist -> `Stopped(ProviderUnavailable)`.
7. Target null, dead, or not hostile -> `WaitingForTarget`.
8. Rules evaluated in order; first rule with all conditions met fires -> `Armed`.
9. No enabled rule with an enabled binding -> `Stopped(IntegrityFailure)`.
10. Reachable rules exist but none match -> `Evaluating` with rejection details.

### Required Binding Semantics

- A binding with `Required = true` is checked only when its level range includes the player's current level (inclusive bounds).
- If any applicable required binding exists but `IsAbilitiesKnown` is false, evaluation stops.
- If an applicable required binding's `AbilityId` is missing from the telemetry ability map, evaluation stops.

## Profile Model (`CombatProfiles.cs`)

See `profiles/README.md` for file-level documentation. The Core types define:

- `CombatProfile`: top-level id, enabled flag, character requirements, abilities map, and rules list.
- `CharacterRequirements`: calling, optional level bounds, and optional build (enabled V5 profiles must omit build).
- `AbilityBinding`: abilityId, key, enabled, required, and optional level bounds.
- `CombatRule`: id, ability alias, enabled flag, conditions, and acknowledgement kind.
- `RuleConditions`: hostility, combat state, health thresholds, resource, and aura conditions.

### Runtime Validation

`CombatProfileLoader.Validate()` checks structural correctness at load time:

- ProfileVersion must be 1.
- At least one ability binding and one rule required.
- Enabled bindings must have non-empty `abilityId` and `key`.
- Rule ability aliases must reference existing bindings.
- Level bounds must be positive and not inverted.
- Enabled profiles must have at least one enabled rule referencing an enabled binding that overlaps the profile level range.
- Enabled profiles must not specify `character.build`.

## Verification

```bash
dotnet build src/BotDs.Core/BotDs.Core.csproj --no-restore
dotnet test tests/BotDs.Tests/BotDs.Tests.csproj --no-restore --filter "FullyQualifiedName~CoreTests|FullyQualifiedName~CoreSafetyTests"
```
