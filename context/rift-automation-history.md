# RIFT Automation History And Sources

## Scope

This dossier records public evidence about historical RIFT automation products, their apparent architectures, and the communities that discussed them. Its purpose is to inform maintainable software design.

Research retrieved: 2026-07-19.

## Publisher Chronology

- **2011 through October 2018**: RIFT was developed and published under Trion Worlds. Historical forum posts, patch notes, bot reports, and enforcement claims from this period are Trion-era evidence.
- **October 22, 2018**: gamigo announced that it acquired the majority of Trion Worlds' assets, including its platform, employees, and full publishing rights for RIFT and other games. The game IPs were acquired by gamigo's sister company Padmapani GmbH for exclusive worldwide use by the group.
- **Current**: gamigo's corporate history says gamigo US Inc. would publish and develop RIFT. The current official RIFT site identifies the game as copyright gamigo US Inc. and links to gamigo support and terms.

Primary sources:

- gamigo acquisition announcement: <https://corporate.gamigo.com/en/presse/gamigo-ag-acquisition-of-trion-worlds-games-company-to-further-strengthen-its-market-position-in-the-games-market/>
- gamigo company history: <https://corporate.gamigo.com/en/about-gamigo/>
- Current RIFT site: <https://riftgame.com/>

Historical Trion statements establish what happened at that time, but they are not current gamigo policy. Current development and account-risk analysis must use current gamigo terms and current technical behavior.

## Main Conclusions

1. RIFT automation existed before launch and developed through several distinct technical approaches.
2. The strongest recovered evidence supports input repeaters and multibox broadcasters, pixel/AutoIt prototypes, external memory-reading programs with keyboard output, and attached .NET hosts with profile and routine systems.
3. RiftMinion developers explicitly planned a heavily customizable Lua bot with one base script per class and a documented function API.
4. No recovered source proves that RiftMinion's Lua ran in RIFT's official addon sandbox. Calling it an official-addon bridge would be unsupported.
5. RIFT's frequent patches were repeatedly identified as the main maintenance burden for memory-dependent products.
6. Historical products consistently separated reusable engine behavior from class routines, ability configuration, or leveling profiles.
7. AutoHotkey and HotkeyNet material must be classified by autonomy: key remapping, key repetition, multibox broadcasting, pixel reaction, and unattended botting are technically different even when they use the same scripting tool.

## Historical Timeline

RIFT-specific AutoHotkey, HotkeyNet, multiboxing, and input-assistance sources are indexed separately in `rift-input-scripting.md`.

### February 2011: AutoIt RIFT Bot Discussion

An AutoIt forum user proposed a RIFT bot before release and identified the core technical questions: health/mana/location observation, background input, and user-authored `GOTO (x,y)` scripts.

- Evidence: contemporary forum post.
- Source type: contemporary secondary.
- Confidence: high that the discussion occurred; no evidence that this product was completed.
- Source: <https://www.autoitscript.com/forum/topic/125316-v3-question-rift-bot/>

This is useful as evidence of the early design split between state acquisition, action output, and profile interpretation.

### March 2011: Basic Pixel Bot

RPG-Exploiters described an AutoIt-tagged RIFT pixel bot that targeted, attacked, looted, healed, and prevented AFK. It could not patrol.

- Evidence: archived article dated 2011-03-25.
- Source type: contemporary secondary.
- Confidence: medium. The public article is descriptive and its members-only implementation was not recovered.
- Original: `http://rpg-exploiters.com/bots-scripts-macros/rift-pixel-bot/`
- Archive: <https://web.archive.org/web/20110405193105id_/http://rpg-exploiters.com/bots-scripts-macros/rift-pixel-bot/>

This represents a screen-observation plus synthetic-input architecture with low game integration and limited state.

### March 2011: MMO Goblin / MacroGoblin

MacroGoblin's archived setup guide is the clearest primary description of an early RIFT memory bot.

Verified features:

- It ran as a RIFT-specific bot on top of the general MacroGoblin host.
- It explicitly read game memory, including character health.
- It downloaded replacement "Game Data" after client patches changed memory locations.
- It used configured key mappings for game actions.
- Combat abilities were ordered by importance and associated with hotkeys.
- It had separate regular, resource-spending, pulling, buff, heal, potion, and pet configurations.
- Heals used health-percentage thresholds.
- Reactive abilities could be triggered by phrases in the RIFT combat log.
- It exposed a global-cooldown timing setting and start/stop/kill hotkeys.

Source type: archived primary product documentation.

Confidence: high.

- Product page index: <https://web.archive.org/cdx/search/cdx?url=macrogoblin.com/rift_bot.aspx&output=json>
- Setup guide: <https://web.archive.org/web/20110303045036id_/http://www.macrogoblin.com/Rift_MMO_Goblin_SetupGuide.htm>
- Pathing guide original: `http://www.macrogoblin.com/Rift_MMO_Goblin_PathingGuide.htm`

The guide directly establishes the maintenance cost of patch-sensitive memory offsets and the viability of data-driven priority lists for combat.

### March-April 2011: RiftMinion Prototype And Planned Rewrite

Recovered RiftMinion forum pages provide primary statements from its administrators.

Verified statements:

- The existing release was described as a prototype.
- A separate "true bot" was under development.
- The planned bot would be heavily customizable with Lua.
- Users would be able to script class behavior, vendoring, and other engine behavior.
- One ready-to-use base script per class was planned.
- A wiki of available functions and methods was planned.
- Developers expected client patches to require rapid bot patches.
- The forum contained separate class-discussion and waypoint-file sections.

Source type: archived primary developer statements.

Confidence: high for the plans and product organization. Confidence is low that every planned feature shipped.

- Customization thread, 2011-04-10 snapshot: <https://web.archive.org/web/20110410151405id_/http://www.riftminion.com/Thread-Whose-interested-in-Customizing-RiftMinion>
- Warrior builds, 2011-04-10 snapshot: <https://web.archive.org/web/20110410162756id_/http://www.riftminion.com/Thread-Warrior-Leveling-builds>
- Archive index: <https://web.archive.org/cdx/search/cdx?url=riftminion.com/*&output=json&filter=statuscode:200&filter=mimetype:text/html>

The Warrior discussion also records a level-44 single-target rotation built from RIFT in-game macros. The macro system selected the first castable ability in an ordered list, while the external bot repeatedly activated macro keys. This is evidence that some early profiles intentionally delegated cooldown selection to RIFT's own macro semantics.

Important limitation: embedded Lua inside a bot host is not the same as an official RIFT addon. The recovered material never identifies the Lua runtime as RIFT's addon API.

### May 2011: RyftoMate / BuddyTeam

RyftoMate was produced by the team associated with GatherBuddy and HonorBuddy.

Verified features and artifacts:

- It required .NET 4 and the Visual C++ 2010 x86 runtime.
- Its UI attached to a running RIFT process.
- Users loaded a profile and selected a class routine.
- Grinding profiles and navigation meshes were separate artifacts.
- A 115-byte `CombatBotProfile.xml` ran class combat routines without route movement.
- The product forum had separate profiles, class guides, routines, support, and patch-update discussions.
- A support report identified a missing `TreeSharp.dll` as the cause of class-routine loading failure.

Source type: archived primary product forum plus contemporary OwnedCore review.

Confidence: high for the artifacts. Use of TreeSharp strongly indicates a behavior-tree implementation, but the exact internal tree and host architecture remain an inference.

- OwnedCore review: <https://www.ownedcore.com/forums/mmo/rift/326380-ryftomate-rift-bot.html>
- Combat assistant: <https://web.archive.org/web/20110528133841id_/http://www.ryftomate.com/profiles/13-combat-assist-bot.html>
- Routine failure and `TreeSharp.dll`: <https://web.archive.org/web/20110603163846id_/http://www.ryftomate.com/support-issues/114-error-can-not-load-routine.html>
- Product archive index: <https://web.archive.org/cdx/search/cdx?url=ryftomate.com/*&output=json&filter=statuscode:200&filter=mimetype:text/html>

### 2011 Product Catalogs

OwnedCore's RIFT catalog, then branded MMOwned, named multiple approaches:

- MacroGoblin with imported path and class-skill files.
- RiftMinion, described by a user as functional but buggy.
- A non-moving pixel bot.
- RIFT Gatherer, an external map utility created before RIFT had a mature addon environment.
- ACT and RiftJunkies combat parsers.

Source type: contemporary secondary discussion.

Confidence: medium. User reports are useful corroboration but not implementation documentation.

- <https://www.ownedcore.com/forums/mmo/rift/322490-rift-bots-programs.html>

### Later Products And Leads

ElitePvPers search indexes identify the following RIFT products or releases:

- Xerbot, described as a mob killer/glider around 2015.
- MoeFish, a fishing bot.
- Manugo Bot.
- A RIFT battleground bot.
- RyftoMate and RiftMinion discussions.

The individual ElitePvPers pages currently require a browser challenge, so architecture claims should remain unverified until archived content is recovered.

- Forum: <https://www.elitepvpers.com/forum/rift-hacks-bots-cheats-exploits/>
- Xerbot lead: <https://www.elitepvpers.com/forum/rift-hacks-bots-cheats-exploits/3582439-release-xerbot-rift-bot-mob-killer-glider.html>

## Communities And Archives

### High-Value RIFT Sources

| Source | Historical value | Evidence caveat |
| --- | --- | --- |
| OwnedCore / MMOwned | Contemporary reviews, product lists, user reports, patch and ban-wave discussion | Mostly secondary evidence and affiliate-era marketing |
| ElitePvPers | RIFT-specific releases and long support threads | Browser challenge limits direct retrieval; archive snapshots needed |
| RiftMinion forum archives | Primary developer plans, class discussions, waypoints, patch reports | Planned features do not prove shipped features |
| RyftoMate forum archives | Primary profiles, routines, support logs, dependencies, patch history | Closed-source internals remain partly inferred |
| MacroGoblin archives | Detailed primary setup and combat configuration documentation | Product is obsolete and navigation is outside this repository's scope |
| RPG-Exploiters archives | Snapshot of pixel-bot and script ecosystem | Some content was paywalled and reports were lightly verified |
| AutoIt forum | Early RIFT automation questions and pixel/input context | AutoIt is distinct from AutoHotkey; the forum prohibited game-bot assistance |
| AutoHotkey forum and archives | RIFT macros, combat repeaters, utility scripts, and fishing automation | Pages may be browser-protected; distinguish assistance from autonomous operation |
| HotkeyNet and Dual-Boxing archives | RIFT multiboxing, key broadcasting, window control, and shared scripts | Broadcasting input is not evidence of game-state observation or autonomous decisions |
| ISBoxer RIFT wiki | RIFT-specific multibox macros and action-bar coordination | Product documentation concerns multiboxing rather than a combat decision engine |
| UnknownCheats | General reversing community and sparse RIFT threads | Recovered RIFT content is thin and often request-only |
| Cheat Engine forum | General historical memory-reading discussions | Not RIFT-specific in the recovered results |
| RebornBuddy / BuddyTeam archives | HonorBuddy/RyftoMate profile, routine, plugin, and bot-base lineage | Later mirrors may mix versions and products |
| CodeDeception / GameDeception lineage | Historical bot and reversing discussions | Treat mirrors and redistributed binaries as untrusted |
| RaGEZONE | Long-running MMO development and reverse-engineering forum | More useful for general protocol/server research than RIFT combat behavior |

### Archive Search Technique

The Internet Archive CDX index remains useful even when a website is gone:

```text
https://web.archive.org/cdx/search/cdx?url=DOMAIN/*&output=json&filter=statuscode:200&filter=mimetype:text/html
```

Use exact snapshot timestamps from CDX to form immutable evidence links:

```text
https://web.archive.org/web/TIMESTAMPid_/ORIGINAL_URL
```

The `id_` form returns the archived response without rewriting links. It is appropriate for source verification but may be harder to browse interactively.

## Current RIFT References

### Current Publisher And Historical Owner

RIFT originated under Trion Worlds. Since the October 2018 asset acquisition, RIFT's publishing and development have been under the gamigo group, specifically gamigo US Inc. Current policies should therefore be attributed to gamigo, while older patch notes and product discussions should be labeled Trion-era.

- <https://corporate.gamigo.com/en/presse/gamigo-ag-acquisition-of-trion-worlds-games-company-to-further-strengthen-its-market-position-in-the-games-market/>
- <https://corporate.gamigo.com/en/about-gamigo/>
- <https://riftgame.com/>

### Official Addon API Archive

The public RIFT addon documentation archive includes unit, ability, event, and UI APIs. It is the primary source for what official addon Lua can observe.

- <https://github.com/360madden/360madden-Rift-Addon-API-Docs>

Current evidence supports observation of visible units, selected targets, hostility, health, resources, casts, available abilities, cooldowns, range, usability, buffs, and combat events. No documented official ability-casting or hostile-target selection command has been found.

A separately supplied local Markdown derivative of an HTML documentation dump provides exact signatures and field tables but does not record its original URL, publication date, or API version. Its combat-relevant findings and limitations are reviewed in `rift-addon-api-corpus.md`; current-client compatibility remains unverified.

### Reader

`Reader` demonstrates a current hybrid in which a RIFT Lua addon serializes selected state to a fixed marker and an external .NET reader finds that marker in process memory.

- <https://github.com/360madden/Reader>

### ChromaLink

`ChromaLink` demonstrates a current hybrid in which a RIFT Lua addon renders encoded pixels and an external .NET application decodes them into HTTP/JSON telemetry.

- <https://github.com/360madden/ChromaLink>

### Current Terms

Gamigo's terms prohibit bots and automation and permit account action. Official API access does not imply permission for an external controller.

- <https://en.gamigo.com/pages/legal>
- <https://assets.cdn.gamigo.com/frontend/agb/Tos_EN_19052026.pdf>

## Unresolved Research Questions

- Did the rewritten RiftMinion Lua bot ship, and if so, was Lua embedded in the external host or connected to another runtime?
- What state and action primitives did RiftMinion expose to class scripts?
- What was the exact schema of RyftoMate class routines and XML combat profiles?
- Are original `CombatBotProfile.xml` and class routine assemblies preserved in a trustworthy archive?
- Did Xerbot use pixels, memory reads, log parsing, an addon, or a hybrid?
- Can current official addon events provide every freshness signal needed for deterministic combat decisions?

## Safety And Trust Notes

- Do not execute historical binaries obtained from forum attachments or archive mirrors.
- Treat old launchers, auto-updaters, and credential prompts as hostile until independently audited.
- Do not store account credentials in this repository.
