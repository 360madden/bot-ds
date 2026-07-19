# RIFT Input Scripting History

## Scope

This document catalogs RIFT-specific AutoHotkey, HotkeyNet, multiboxing, key-broadcast, and input-assistance material. These tools span very different levels of autonomy and must not all be labeled as bots.

Research retrieved: 2026-07-19.

## Classification

### Input Assistance

Examples include key remapping, mouse-look toggles, alternate action-bar bindings, and window-specific hotkeys. The player still decides each action.

### Key Repetition

A script repeatedly activates one key or RIFT in-game macro. RIFT's macro system may select the first castable ability, but the external script itself may have no game-state model.

### Multibox Broadcasting

HotkeyNet and similar tools send one user's keypress to multiple RIFT windows. This coordinates clients but does not necessarily make target or combat decisions.

### Reactive Automation

A script reacts to pixels, images, logs, or timers. This adds partial state and may automate cooldown, healing, fishing, or combat behavior.

### Autonomous Botting

A loop observes or assumes state, chooses actions, and continues without a player issuing each command. Tool choice does not change the autonomy classification.

## AutoHotkey Evidence

### RIFT Mentalist Combat Script

The AutoHotkey forum thread "R~I~F~T simple script for the LEET Mentalist build," published September 25, 2013, contains the complete script and usage description. While the user holds Shift, it repeatedly sends a configured RIFT macro key. It also samples one configured screen pixel associated with the TargetCastBar addon and sends a separate interrupt key when the pixel matches the configured cast-bar color. Additional bindings map the mouse wheel to two action keys while preserving normal scrolling with Ctrl.

- Source type: contemporary primary source code and author description.
- Confidence: high.
- Source: <https://www.autohotkey.com/board/topic/97714-rift-simple-script-for-the-leet-mentalist-build/>

This is reactive combat assistance: a hybrid of a held-key repeater, RIFT's ordered in-game macro, and one addon-rendered pixel signal. It is not evidence of structured external state observation or unattended target selection. The script is gated to the active RIFT window through its window class.

### RIFT Macro Help

The AutoHotkey forum thread "Macro Help For Rift," published April 22, 2012, is a request for help activating RIFT on a second monitor and then continuing an input script. The author's attempts based on mouse clicks, Alt-Tab, and `WinActivate` did not work as intended. The only reply points generally toward a Windows API call; the thread contains no completed solution.

- Source type: contemporary primary user report.
- Confidence: high for the reported attempts; no implemented technique was established.
- Source: <https://www.autohotkey.com/board/topic/79702-macro-help-for-rift/>

### RIFT Fishing Bot

The two-page AutoHotkey thread "Rift - Fishing Bot Simplistic (Modified)," published December 11, 2012, contains source code for an unattended fishing loop. It activates the RIFT window, casts and clicks at configured coordinates, observes changes in the Windows mouse-cursor handle, and reads RIFT's text log for catch, miss, stopped-fishing, and expired-lure messages. It also exposes explicit pause, reload, bait, and exit hotkeys.

- Source type: contemporary primary source code, author description, and user reports.
- Confidence: high.
- Source: <https://www.autohotkey.com/board/topic/88039-rift-fishing-bot-simplistic-modified/>
- Page 2: <https://www.autohotkey.com/board/topic/88039-rift-fishing-bot-simplistic-modified/page-2>

This is log- and cursor-reactive autonomous automation rather than blind key timing. Replies document failures caused by localized log text, disabled logging, and custom log-file locations. Those reports reinforce that logs provide event evidence but require environment validation and do not constitute a complete current-state model.

### RIFT Mouse Look Toggle

The Mouse Look Toggle project combined a RIFT addon with an AutoHotkey script. The addon communicated UI state to AutoHotkey by rendering a few pixels in a corner of the game window. The external script only operated in the active RIFT window and changed behavior when chat or supported UI windows were open.

- Source type: primary project documentation.
- Confidence: high.
- Source: <http://fabd.github.io/rift-mouselook-addon/>

This is a strong historical precedent for official-addon-to-external-process optical communication. Its goal was input assistance, not autonomous combat.

The project also quotes a January 2013 Trion patch note acknowledging innocent use of keyboard-assistance software while responding to bot activity. This is Trion-era historical policy evidence, not current gamigo policy.

### Public RIFT AutoHotkey Gist

A public gist titled "rift autohotkey" maps gamepad and numpad controls to a 70 ms key-repeat loop and includes explicit stop/suspend controls.

- Source type: public source code.
- Confidence: high for what the code does; unknown provenance and date reduce its historical value.
- Source: <https://gist.github.com/Bamux/a425d5668ba53e2aea2dccf492bf2b62>

This is a blind key repeater. It contains no target, health, cooldown, or combat-state observation.

### Later RIFT Macro Discussion

A 2022 Reddit result describes AutoHotkey menu navigation for RIFT class/role switching.

- Source type: modern secondary discussion.
- Confidence: medium.
- Source: <https://www.reddit.com/r/AutoHotkey/comments/zqd7fy/rift_macros_and_shortcuts/>

## HotkeyNet And Multiboxing Evidence

### RIFT HotkeyNet Videos

Search indexes preserve RIFT-specific HotkeyNet multiboxing videos, including "EASY Rift 2x Multiboxing on solo computer with Hotkeynet (HKN)" and a broader RIFT multibox guide.

- Source type: contemporary demonstration indexed by web search.
- Confidence: medium that HotkeyNet was used for RIFT multiboxing; the videos were not independently transcribed during this review.
- Two-client HotkeyNet demonstration: <https://www.youtube.com/watch?v=zrADig-bp00>
- RIFT multibox guide: <https://www.youtube.com/watch?v=vjFClTGOb00>

### Dual-Boxing RIFT Forum

Dual-Boxing maintained a dedicated RIFT forum, and its HotkeyNet guide explains the general trigger-and-broadcast scripting model used by multiboxers.

- Source type: contemporary specialist community.
- Confidence: high that the RIFT section and HotkeyNet guidance existed; individual RIFT scripts still require page-level recovery.
- RIFT forum: <https://www.dual-boxing.com/forums/48-Rift>
- HotkeyNet guide: <https://www.dual-boxing.com/threads/16177-Guide-HowTo-use-HotKeyNet-for-boxing>

### ISBoxer RIFT Documentation

ISBoxer published a RIFT-specific multibox macro walkthrough covering auto-assist, action bars, follow controls, and operation from different windows.

- Source type: primary product documentation.
- Confidence: high for the documented multibox workflow.
- Source: <https://isboxer.com/wiki/RIFT:Basic_Multiboxing_Macros_Walkthrough>

### HotkeyNet Archive Limitation

No directly indexed RIFT page on `hotkeynet.com` was recovered in this research pass. Evidence currently comes from RIFT demonstrations and specialist multibox communities that explicitly identify HotkeyNet. This should remain distinct from a primary HotkeyNet-hosted RIFT script archive until exact pages are found.

## Technical Lessons

- RIFT's in-game macro language can collapse several abilities behind one key by selecting the first castable line.
- External repeaters can appear to make cooldown decisions when the decision is actually delegated to the RIFT macro.
- Key broadcasters and repeaters are action executors, not state providers.
- Window focus and explicit suspend/stop controls recur across input scripts and should inform fail-safe requirements.
- Pixel-based addon communication existed in the RIFT community before current Reader/ChromaLink work.
- Log parsing can provide event evidence but is not a complete current-state model.
- Resolution, window mode, UI position, and chat focus are recurring failure modes in pixel and input scripts.

## Publisher Context

Most historical sources in this document are from the Trion Worlds era. gamigo acquired the majority of Trion Worlds' assets and full RIFT publishing rights on October 22, 2018. Current compatibility and policy conclusions must be based on current gamigo sources.

- Acquisition announcement: <https://corporate.gamigo.com/en/presse/gamigo-ag-acquisition-of-trion-worlds-games-company-to-further-strengthen-its-market-position-in-the-games-market/>
- gamigo history: <https://corporate.gamigo.com/en/about-gamigo/>
- Current RIFT site: <https://riftgame.com/>

## Research Leads

- Recover exact RIFT scripts from old HotkeyNet mirrors or forum attachments without executing binaries.
- Search archived Dual-Boxing RIFT thread indexes for HotkeyNet script text and dates.
- Recover the full Trion January 2013 patch note quoted by Mouse Look Toggle.
- Determine whether additional RIFT AutoHotkey combat scripts used pixels, combat logs, or only timers.
- Search MPGH and OwnedCore for mirrored RIFT multibox script text while treating downloads as untrusted.

## Trust Boundary

- Do not execute downloaded `.ahk`, HotkeyNet, launcher, or compiled script files without review.
- Do not infer current gamigo permission from Trion-era tolerance of keyboard-assistance tools.
