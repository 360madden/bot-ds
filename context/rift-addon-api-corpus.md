# Local RIFT Addon API Corpus

## Source Record

- Title: `LLM_RIFT_API` Markdown documentation corpus
- Source type: local derivative of an uploaded HTML documentation dump
- Local path: `C:\work\LLM_RIFT_API`
- Original URL: not recorded in the corpus
- Archive URL and timestamp: not recorded in the corpus
- Published date: unknown
- Retrieved date: 2026-07-19
- Confidence: high for the text present in the raw Markdown pages; medium for current-client applicability
- Related public archive: <https://github.com/360madden/360madden-Rift-Addon-API-Docs>

The local corpus does not establish that it was generated from the related public archive. Treat that relationship as unverified until the underlying HTML files or content hashes are compared.

## Provenance And Quality Limits

`MASTER_INDEX.md` describes the package as a Markdown-only derivative of an uploaded HTML dump and says it is optimized for LLM-assisted addon coding. Each raw page records an original HTML filename and category index, but no page reviewed records an original publisher, URL, publication date, game version, API version, or extraction date.

The package distinguishes synthesized overview and index pages from raw converted pages. It explicitly directs readers to use raw pages for exact signatures, result tables, and event payloads.

The conversion is not lossless:

- Several buff event parameter types appear as `<nope>`.
- `UI.CreateFrame` lists more frame types in its prose than in its generated parameter description.
- Some summaries refer to older event hierarchy names that do not match the page title.

These defects do not invalidate clear field descriptions, but they make runtime conformance tests mandatory. This corpus is evidence of a documented API surface, not proof that every entry still works in the current gamigo client.

## Unit And Target Observation

### Verified

- `Inspect.Unit.List()` returns a map of all units the client can see from unit ID to one unit specifier. Source: `Unit/inspect_unit_list.md`.
- Unit specifiers can begin with `player`, `focus`, `mouseover`, `group01` through `group20`, or a unit ID, followed by `.target`, `.pet`, or `.owner` chains. This provides a documented way to inspect `player.target`. Source: `Miscellaneous/unit.md`.
- `Inspect.Unit.Detail(unit)` accepts a unit ID or specifier and returns identity, classification, combat, resource, and coordinate fields. Source: `Unit/inspect_unit_detail.md`.
- Combat-relevant fields include `id`, `name`, `level`, `player`, `relation`, `health`, `healthMax`, `healthCap`, `combat`, `energy`, `energyMax`, `mana`, `manaMax`, `power`, `charge`, `chargeMax`, `combo`, `focus`, `spirit`, `coordX`, `coordY`, `coordZ`, `radius`, `pvp`, `tagged`, and `tier`.
- `relation` may be `hostile` or `friendly`; neutral units omit it. `player` distinguishes players from NPCs.
- Unit health, maximum health, combat state, resources, coordinates, availability, and unit-specifier changes have documented event families. Sources: `Unit/INDEX.md` and the individual `Unit/event_*.md` pages.
- `Utility.Unit.Availability()` reports which detail members are available at each availability level. Source: `Unit/utility_unit_availability.md`.

### Limits

- `aggro` and line-of-sight `blocked` are documented only for group members, not arbitrary hostile targets.
- The detail table has no explicit `dead`, `targetable`, or `attackable` member in this corpus.
- A unit disappearing from availability is not equivalent to a confirmed death. `Event.Combat.Death` is the documented death signal.
- "All units that the client can see" does not define range, phasing, occlusion, update latency, or completeness. Those behaviors require current-client tests.
- The event docs use `false` in place of some `nil` values. A normalized provider must preserve unknown or unavailable state rather than coercing it to zero or `false` indiscriminately.

## Cast And Combat Observation

### Verified

- `Inspect.Unit.Castbar(unit)` returns ability ID and name when available, frame-relative start time, channel status, duration, expiration lag, remaining time, and an uninterruptible flag. Source: `Unit/inspect_unit_castbar.md`.
- `Event.Unit.Castbar` signals cast-bar visibility changes and identifies affected units. Source: `Unit/event_unit_castbar.md`.
- Combat events cover damage, death, dodge, healing, immunity, miss, parry, and resist. Source: `Combat/INDEX.md`.
- Damage events include caster and target IDs, ability ID and name, actual damage, absorption, blocking, interception, modification, overkill, critical-hit status, and damage type. Source: `Combat/event_combat_damage.md`.
- Death events identify the target and optional initiator. Source: `Combat/event_combat_death.md`.

Combat events provide event history, not a complete current-state snapshot. State reconstruction should resynchronize through `Inspect.*` calls after startup, dropped frames, availability changes, or provider recovery.

## Ability And Progression Observation

### Verified

- `Inspect.Ability.New.List()` returns IDs of available player abilities. Source: `Ability/inspect_ability_new_list.md`.
- `Event.Ability.New.Add` and `.Remove` signal changes to that available set. Sources: `Ability/event_ability_new_add.md` and `Ability/event_ability_new_remove.md`.
- `Inspect.Ability.New.Detail(ability)` returns identity, cast style, costs, cooldown state, range, current target, and usability. Source: `Ability/inspect_ability_new_detail.md`.
- Relevant fields include `id`, `idNew`, `name`, `castingTime`, `channeled`, `continuous`, `cooldown`, current cooldown begin/duration/remaining/paused/expired, resource costs and charge gain, `rangeMin`, `rangeMax`, `outOfRange`, `target`, `unusable`, `passive`, `positioned`, `stealthRequired`, and weapon requirement.
- Events signal cooldown begin/end, in-range/out-of-range, usable/unusable, and current-target changes. Source: `Ability/INDEX.md`.

This directly supports runtime reconciliation of a combat profile against currently available abilities. It does not provide a documented level or build requirement for each ability, so profile requirements remain declarative data and discovered availability remains the runtime authority.

### Limits

- The corpus does not document a dedicated global-cooldown value. `currentCooldownDuration` is described only as the cooldown currently influencing an ability.
- `unusable` and `outOfRange` are useful vetoes but the corpus does not enumerate every reason an ability may be unusable.
- No action-bar slot, key binding, or hotkey inspection API was found. Native action-bar pages identify UI frames but expose no documented slot-to-ability mapping.

## Buff And Debuff Observation

### Verified

- `Inspect.Buff.List(unit)` lists buff IDs on a unit. Source: `Buff/inspect_buff_list.md`.
- `Inspect.Buff.Detail(unit, buff)` provides the creating ability when available, caster, start time, duration, remaining time, expiration lag, stack count, type, and debuff/curse/disease/poison flags. Source: `Buff/inspect_buff_detail.md`.
- Add, change, and remove events are documented. Source: `Buff/INDEX.md`.

The malformed parameter types in the converted buff event pages require validation against actual callback values before defining a provider schema.

## Timing, Version, And Freshness

- `Inspect.Time.Frame()` returns the game time of the last frame and remains constant until the next frame. Source: `Time/inspect_time_frame.md`.
- `Event.System.Update.Begin` and `.End` delimit frame rendering. Sources: `System/event_system_update_begin.md` and `System/event_system_update_end.md`.
- `Inspect.System.Version()` returns client build, external version, and internal version. Source: `System/inspect_system_version.md`.
- `Inspect.System.Secure()` and secure enter/leave events expose secure-mode state. Entering secure mode is described as usually equivalent to entering combat. Sources: `System/inspect_system_secure.md` and `System/event_system_secure_enter.md`.

An addon telemetry schema can therefore include client version and frame time. Sequence numbers, schema version, heartbeat, producer timestamp, and maximum accepted age are still application protocol requirements rather than built-in guarantees.

## Addon UI As An Optical Transport

### Verified Building Blocks

- Addons can create UI contexts and frames through `UI.CreateContext` and `UI.CreateFrame`.
- Frames can be positioned and sized through `SetPoint`, `SetWidth`, and `SetHeight`.
- Frames and textures can be assigned numeric RGBA background colors.
- UI can be changed at frame boundaries through system update events.

Sources: `UI/ui_createcontext.md`, `UI/ui_createframe.md`, `Frame/frame_setpoint.md`, `Frame/frame_setwidth.md`, `Frame/frame_setheight.md`, `Frame/frame_setbackgroundcolor.md`, and `System/event_system_update_begin.md`.

Inference: these primitives are sufficient to construct addon-controlled colored cells suitable for an optical telemetry protocol. The corpus does not define such a protocol or establish capture reliability, color fidelity, pixel scaling, latency, or current secure-mode behavior. ChromaLink remains the direct implementation evidence for that architecture.

## Documented Action Boundary

No ability-casting command or hostile-target selection command was found in this corpus.

- The Ability category contains inspection and events only; it has no `Command.Ability.*` entry.
- The only Unit command is `Command.Unit.Menu`, which opens a standard context menu for a unit.
- `Command.Cursor` can place an ability on the cursor only outside secure mode; it is not documented as casting the ability.
- Native action-bar pages identify built-in UI frames but do not document invoking their buttons.

This is strong evidence that the documented addon surface is observation-oriented for combat. Because the corpus has unknown date, version, and completeness, absence here is not universal proof that no command ever existed. Current-client runtime inspection and an authoritative current source remain necessary. Automation through external synthetic input is a separate action mechanism and remains prohibited by current gamigo terms regardless of whether observation uses the official addon API.

## Planning Implications

- An addon-backed provider can potentially observe the core state needed by the intended combat engine: player and selected-target identity, relation, health, resources, combat state, casts, available abilities, cooldowns, range, usability, and buffs.
- The provider must model field availability and unknown values explicitly.
- Snapshot reads and change events should be combined; neither should be treated as independently complete.
- Ability discovery can drive progression reconciliation, but binding validation requires a separate mechanism or explicit configuration.
- Target selection and ability execution remain unresolved action-output decisions.
- A current-client conformance fixture must verify every selected field, event, secure-mode behavior, update rate, and failure mode before implementation relies on this corpus.
- This evidence does not select Reader, ChromaLink, another addon bridge, or any action actuator, and it does not bypass the implementation planning gate.
