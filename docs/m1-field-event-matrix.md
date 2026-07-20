# M1 Field/Event Matrix

Status: **Structure ready** — awaiting live conformance probe data

Date: 2026-07-20

## Purpose

This matrix records every RIFT addon API field consumed by BotDs telemetry. For each field, it documents the observed type, nil vs false behavior, update trigger, knownness semantics, cadence, and secure-mode behavior. This data drives protocol design, freshness rules, and the Core evaluator's knownness model.

## How to Populate

1. Install `addons/BotDsConformance` into RIFT
2. Run `/reloadui` in-game
3. Extract the probe output from the RIFT client log
4. Fill in the **Observed Value** and **Confidence** columns below
5. Mark unobservable fields as **Rejected** with reason

---

## 1. System Information

| Field | API Call | Expected Type | Observed Value | Nil Behavior | Secure-Mode | Confidence |
|-------|----------|---------------|----------------|--------------|-------------|------------|
| Frame time | `Inspect.Time.Frame()` | number (seconds) | _TBD_ | _TBD_ | Always available | — |
| Client version | `Inspect.System.Version()` | string | _TBD_ | _TBD_ | _TBD_ | — |
| Secure mode | `Inspect.System.Secure()` | boolean | _TBD_ | _TBD_ | Always true when active | — |

## 2. Unit State — Player

| Field | API Call | Expected Type | Observed Value | Nil Behavior | Update Trigger | Confidence |
|-------|----------|---------------|----------------|--------------|----------------|------------|
| `id` | `Inspect.Unit.Detail("player")` | string | _TBD_ | nil when unavailable | Unit change | — |
| `name` | same | string | _TBD_ | nil when unavailable | Name change | — |
| `level` | same | number | _TBD_ | nil when unavailable | Level up | — |
| `calling` | same | string | _TBD_ | nil? | Never changes | — |
| `player` | same | boolean | _TBD_ | false for NPCs | Never changes | — |
| `relation` | same | "hostile"/"friendly" | _TBD_ | nil for neutral | Relation change | — |
| `combat` | same | boolean | _TBD_ | false when out of combat | Combat enter/leave | — |
| `health` | same | number | _TBD_ | nil when unavailable | Damage/heal | — |
| `healthMax` | same | number | _TBD_ | nil when unavailable | Stat change | — |
| `mana` | same | number | _TBD_ | nil if no mana | Mana change | — |
| `manaMax` | same | number | _TBD_ | nil if no mana | Stat change | — |
| `energy` | same | number | _TBD_ | nil if no energy | Energy change | — |
| `energyMax` | same | number | _TBD_ | nil if no energy | Stat change | — |
| `power` | same | number | _TBD_ | nil if no power | Power change | — |
| `charge` | same | number | _TBD_ | nil if no charge | Charge change | — |
| `chargeMax` | same | number | _TBD_ | nil if no charge | Stat change | — |
| `focus` | same | number | _TBD_ | nil if no focus | Focus change | — |
| `focusMax` | same | number | _TBD_ | nil if no focus | Stat change | — |
| `spirit` | same | number | _TBD_ | nil if no spirit | Spirit change | — |
| `pvp` | same | boolean | _TBD_ | false? | PvP flag change | — |
| `healthCap` | same | number | _TBD_ | nil? | Stat change | — |
| `tagged` | same | boolean | _TBD_ | false | Tag change | — |

## 3. Unit State — Target

| Field | API Call | Expected Type | Observed Value | Nil Behavior | Update Trigger | Confidence |
|-------|----------|---------------|----------------|--------------|----------------|------------|
| `id` | `Inspect.Unit.Detail("player.target")` | string/nil | _TBD_ | nil = no target | Target change | — |
| `name` | same | string/nil | _TBD_ | nil = no target | Target change | — |
| `level` | same | number/nil | _TBD_ | nil = no target | Target change | — |
| `player` | same | boolean/nil | _TBD_ | nil/false for NPCs | Target change | — |
| `relation` | same | string/nil | _TBD_ | nil = neutral or unknown | Relation change | — |
| `combat` | same | boolean/nil | _TBD_ | TBD | Combat state change | — |
| `health` | same | number/nil | _TBD_ | nil if unknown | Damage/heal | — |
| `healthMax` | same | number/nil | _TBD_ | nil if unknown | Stat change | — |
| `dead` | same | ?? | _TBD_ | TBD — may not exist directly | Death event | — |

**Critical distinction**: Target `nil` (no selection) vs target detail `nil` (inspection failure) vs target with `relation = nil` (neutral). These must remain distinct in the Core model.

## 4. Abilities

| Field | API Call | Expected Type | Observed Value | Nil Behavior | Update Trigger | Confidence |
|-------|----------|---------------|----------------|--------------|----------------|------------|
| List count | `Inspect.Ability.New.List()` | table (array) | _TBD_ | nil if unavailable | Ability add/remove | — |
| `id` | `Inspect.Ability.New.Detail(id)` | string | _TBD_ | — | — | — |
| `name` | same | string | _TBD_ | — | — | — |
| `castingTime` | same | number | _TBD_ | nil if instant | — | — |
| `channeled` | same | boolean | _TBD_ | false | — | — |
| `cooldown` | same | number? | _TBD_ | nil if no cooldown | Cooldown change | — |
| `currentCooldownRemaining` | same | number | _TBD_ | nil/-1 if not on CD | Each frame | — |
| `currentCooldownDuration` | same | number | _TBD_ | nil/-1 | Cooldown start | — |
| `usable` | same | boolean | _TBD_ | TBD | Usability change | — |
| `unusable` | same | boolean | _TBD_ | TBD | Usability change | — |
| `outOfRange` | same | boolean | _TBD_ | TBD | Range change | — |
| `rangeMin` | same | number | _TBD_ | nil if melee | — | — |
| `rangeMax` | same | number | _TBD_ | nil if melee | — | — |
| `passive` | same | boolean | _TBD_ | false | — | — |
| `continuous` | same | boolean | _TBD_ | false | — | — |
| `positioned` | same | boolean | _TBD_ | false | — | — |
| `stealthRequired` | same | boolean | _TBD_ | false | — | — |
| `target` | same | string? | _TBD_ | nil if no target | Target change | — |
| `idNew` | same | string | _TBD_ | same as id? | — | — |
| `cooldown` | same | number? | _TBD_ | nil if no cooldown | — | — |

## 5. Castbar

| Field | API Call | Expected Type | Observed Value | Nil Behavior | Update Trigger | Confidence |
|-------|----------|---------------|----------------|--------------|----------------|------------|
| `abilityId` | `Inspect.Unit.Castbar(unit)` | string | _TBD_ | nil if not casting | Cast start/end | — |
| `name` | same | string | _TBD_ | nil | Cast start/end | — |
| `remaining` | same | number | _TBD_ | nil | Each frame | — |
| `duration` | same | number | _TBD_ | nil | Cast start | — |
| `channel` | same | boolean | _TBD_ | false | Cast type | — |
| `uninterruptible` | same | boolean | _TBD_ | false | Cast flag | — |

## 6. Auras (Buffs/Debuffs)

| Field | API Call | Expected Type | Observed Value | Nil Behavior | Update Trigger | Confidence |
|-------|----------|---------------|----------------|--------------|----------------|------------|
| List count | `Inspect.Buff.List(unit)` | table (array) | _TBD_ | nil if unavailable | Buff add/remove | — |
| `buffId` | `Inspect.Buff.Detail(unit, id)` | string | _TBD_ | — | — | — |
| `name` | same | string | _TBD_ | — | — | — |
| `stacks` | same | number | _TBD_ | 0 or nil | Stack change | — |
| `remaining` | same | number (ms?) | _TBD_ | nil if permanent | Each frame | — |
| `debuff` | same | boolean | _TBD_ | false | — | — |
| `curse` | same | boolean | _TBD_ | false | — | — |
| `disease` | same | boolean | _TBD_ | false | — | — |
| `poison` | same | boolean | _TBD_ | false | — | — |
| `caster` | same | string? | _TBD_ | nil if self | — | — |

## 7. Action Bars

| Field | API Call | Expected Type | Observed Value | Nil Behavior | Update Trigger | Confidence |
|-------|----------|---------------|----------------|--------------|----------------|------------|
| Current page | `Action.Bar.Page.Get()` | number | _TBD_ | TBD | Page change | — |
| Slot N type | `Action.Get(N)` | string ("ability"/"macro"/nil) | _TBD_ | nil = empty slot | Bar change | — |
| Slot N ability ID | `Action.Get(N).id` | string | _TBD_ | nil = not an ability | Bar change | — |

**Finding**: Key bindings to action-bar slots are NOT observable. `Command.Bind` exists but the API does not expose a query for existing bindings. Therefore: action-bar/key mappings must be **user-configured in profiles** and verified via controlled calibration.

## 8. Known Gaps (Likely Rejected)

| Field | Reason |
|-------|--------|
| Global cooldown (GCD) | No dedicated GCD API. Must infer from cooldown state of all abilities. |
| `aggro` / threat | Only documented for group members, not arbitrary targets |
| Line-of-sight `blocked` | Only documented for group members |
| Explicit `dead` flag on unit detail | May not exist; death detected via `Event.Combat.Death` or health = 0 |
| Chat/edit focus state | Unknown; may require UI frame inspection or keyboard-focus API testing |
| Key-binding screen active | Unknown |
| Loading screen active | Unknown; may be detectable via frame-time stall or unit-list emptiness |
| Modal UI active | Unknown |

These gaps need live-client investigation. If unobservable, the Core evaluator must treat them as `GameInputReady = false` when any of these states is suspected, defaulting to the safest (blocked) interpretation.

## 9. Typed Acknowledgement Feasibility

| Acknowledgement Type | Requires | Likely Feasible? | Notes |
|---------------------|----------|-----------------|-------|
| Exact ability cast start | Castbar `abilityId` transition from nil → specific ID | **Likely** | Castbar API returns abilityId |
| Ability-specific cooldown | `currentCooldownRemaining` transition from 0 → >0 | **Likely** | Per-ability cooldown tracking |
| Shared GCD | No dedicated GCD field | **Difficult** | Must detect many abilities going on cooldown simultaneously |
| Aura add/remove | `Buff.List` change + `Buff.Detail` comparison | **Likely** | Buff add/remove events exist |
| Resource change | Resource field delta | **Likely** | Direct field comparison |
| Combat event | `Event.Combat.*` callbacks | **Likely** | Event system available |
| Aura stack change | `stacks` field delta | **Likely** | Direct field comparison |

## 10. Resource Kinds Observed

Document which resource kinds actually appear for the current Warrior:

| Resource Kind | Observed? | Value Range | Notes |
|--------------|-----------|-------------|-------|
| `power` | _TBD_ | _TBD_ | Expected: Warrior primary resource |
| `mana` | _TBD_ | _TBD_ | Expected: nil for Warrior |
| `energy` | _TBD_ | _TBD_ | Expected: nil for Warrior |
| `charge` | _TBD_ | _TBD_ | Expected: nil for Warrior |
| `focus` | _TBD_ | _TBD_ | Hunter/rogue resource |
| `spirit` | _TBD_ | _TBD_ | Healer resource |
| `combo` | _TBD_ | _TBD_ | Combo points (if Warrior spec uses them) |

## 11. Payload Size Estimates

| Section | Typical Records | Record Size | Typical Bytes | Worst Case |
|---------|----------------|-------------|---------------|------------|
| ProviderInfo | 1 | ~28-156 | 28 | 156 (max client version) |
| Player | 1 | ~120 | _TBD_ | _TBD_ |
| Target | 0-1 | ~120 | _TBD_ | _TBD_ |
| Abilities | _TBD_ | 46 fixed | _TBD_ | 128 × 46 = 5,888 |
| Player Auras | _TBD_ | 70 fixed | _TBD_ | 64 × 70 = 4,480 |
| Target Auras | _TBD_ | 70 fixed | _TBD_ | 64 × 70 = 4,480 |
| **Total per frame** | | | _TBD_ | ~15,000 (V5) or much less (optical) |

For optical transport, not all sections need to be sent every frame. A differential/cadenced approach is likely required to fit within optical bandwidth constraints.
