# Development Context

This directory preserves product research, historical evidence, current references, and unresolved technical questions for humans and AI agents working on this repository.

It is documentation only. It does not approve an implementation architecture or bypass the planning gate in `../AGENTS.md`.

## Reading Order

1. Read `../AGENTS.md` for binding scope and planning constraints.
2. Read `rift-automation-history.md` for RIFT-specific historical and current evidence.
3. Read `rift-input-scripting.md` for AutoHotkey, HotkeyNet, multiboxing, and input-assistance history.
4. Read `rift-addon-api-corpus.md` for the combat-relevant official-addon observation surface and its provenance limits.
5. Read `architecture-evidence.md` for patterns found across RIFT and other PC MMO automation ecosystems.

## Documents

- `rift-automation-history.md`: RIFT products, communities, archives, source quality, and current references.
- `rift-input-scripting.md`: RIFT-specific AutoHotkey, HotkeyNet, multiboxing, and addon-to-input bridge evidence.
- `rift-addon-api-corpus.md`: local addon API corpus provenance, combat-observation capabilities, action boundary, and validation requirements.
- `architecture-evidence.md`: observed architecture patterns and implications for future planning.

## Evidence Labels

- **Primary**: official documentation, source code, or an archived statement from the product developer.
- **Contemporary secondary**: a review, forum report, or guide written while the product existed.
- **Modern secondary**: a later summary or search index describing historical material.
- **Inference**: a conclusion drawn from artifacts. It must not be presented as a verified implementation fact.
- **Unverified lead**: a useful search direction for which the underlying content has not been recovered.

## Confidence Levels

- **High**: directly stated in primary material or visible in source code.
- **Medium**: corroborated by multiple contemporary sources or strongly implied by named artifacts.
- **Low**: based on one user report, a search snippet, or an inaccessible page.

## Research Rules

- Include the original URL and an Internet Archive snapshot when available.
- Record the retrieval date.
- Preserve historical dates and product versions.
- Separate what a product did from how it was implemented.
- Do not infer that a Lua-based bot used RIFT's official addon sandbox. Embedded Lua, injected Lua, and official addon Lua are different execution environments.
- Do not treat historical operation as evidence of current compatibility.
- Do not treat use of an official observation API as permission for external automation.
- Do not preserve credentials, binaries, or malware samples.

## Updating Context

New research entries should include:

```text
Title:
Source type:
Original URL:
Archive URL and timestamp:
Published date:
Retrieved date:
Claim or artifact:
Confidence:
Notes and limitations:
```

Last reviewed: 2026-07-19.
