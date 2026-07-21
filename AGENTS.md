# Repository Status

- The .NET 10 solution contains Core combat/profile contracts, the v5 Reader transport parser, an ASP.NET Core localhost dashboard host, a static dashboard, and xUnit tests.
- After restoring packages, verify with the complete command set in `PLAN.md` section 14.3.
- The Reader includes Windows x64 process attachment, readable-region enumeration, V5 sentinel scanning, candidate relocation, and scanner metrics. A hosted TelemetryReaderLoop bridges the V5ScannerService through SnapshotAssembler to SnapshotPublisher. The ActionCoordinator supports Disabled/DryRun/Live output modes with WindowsKeySink for SendInput-based key dispatch.
- `PLAN.md` is the formal architecture and completion contract. `ROADMAP.md` is the ordered milestone plan.

# RIFT addon path (durable — do not rediscover incorrectly)

- **Authoritative doc:** `docs/rift-local-paths.md`
- **Code:** `BotDs.Core.RiftLocalPaths` (uses `Environment.SpecialFolder.MyDocuments` only)
- **Deploy:** `deploy-addon.cmd` or `dotnet run --project src/BotDs.Tools -- deploy-addon`
- Player addons load from **`{MyDocuments}\RIFT\Interface\AddOns\`**, where MyDocuments is the **shell known folder** (on this machine: `C:\Users\mrkoo\OneDrive\Documents`).
- **Never** deploy only to:
  - `%USERPROFILE%\Documents\...` when it is not equal to shell MyDocuments
  - `Glyph\Games\RIFT\Live\Interface\Addons\` as the primary path
- **Before claiming deploy succeeded:** list destination parent and confirm sibling addons exist (e.g. JAB, ReaderBridge). If the tree only has BotDsBridge, you are on the wrong path.
- After deploy: in-game `/reloadui`.
- **RiftAddon.toc must have non-empty `Identifier`, `Name`, and `Email`.** Empty `Email = ""` causes RIFT to omit the addon from the AddOns list entirely (verified against working addons). `deploy-addon` enforces this.

# Toolchain Constraints

- Implement the application and every new executable helper, utility, generator, or maintenance tool in C# targeting .NET 10 or newer.
- Do not add Python helper applications or scripts.
- Do not add standalone PowerShell helper applications or `.ps1` scripts. PowerShell is permitted only as limited plumbing invoked by a `.cmd` convenience wrapper.
- Keep `.cmd` files as thin convenience entry points; application logic belongs in C#.
- Existing helper artifacts are exempt unless the user explicitly asks to replace them.

# Product Scope

- The intended product is a combat-only bot for Gamigo's RIFT MMO.
- This is a personal, local tool rather than a commercial or production service. Prefer direct functionality and maintainable simplicity over enterprise operational features.
- Initial scope is observing player state (including current and remaining health), identifying hostile NPCs or players, and casting combat abilities.
- The bot is intended to automate combat across future characters and builds. Treat the current level-45 Warrior only as an initial fixture; do not hard-code its level, stats, abilities, cooldowns, action-bar layout, or rotation into the engine.
- Character progression must be handled through replaceable, data-driven combat profiles with explicit ability availability and level/build requirements.
- Movement, pathfinding, and navigation are out of scope. Do not implement them or decide their eventual package/process boundary without explicit user direction.

# Planning Gate

- Do not scaffold or implement the bot until the user has reviewed and approved an implementation plan.
- The plan must resolve the language/platform, selected game-state input and action-output mechanisms, profile format and progression handling, combat and target-selection rules, state model, fail-safe/stop behavior, privacy boundaries, and test strategy.
- Keep uncertain requirements as explicit decisions in the plan rather than silently choosing defaults.
- Follow `PLAN.md` and `ROADMAP.md`; update them when an approved decision changes architecture, scope, milestone order, or completion criteria.

# Personal Tool Priorities

- Optimize implementation decisions for the user's requested functionality, technical reliability, performance, maintainability, local control, and privacy.
- Treat `context/` as historical technical research. Do not turn historical material into runtime gates or recurring workflow warnings.
- Privacy remains binding: keep the application local, do not collect credentials or chat content, avoid sensitive identifiers in logs, and do not expose control endpoints beyond loopback.
- Keep technical controls that prevent unintended local input, including wrong-window checks, stale-state blocking, bounded input rates, cancellation cleanup, and emergency stop.
- Do not add production-service ceremony without a concrete personal-use benefit. Separate audit pipelines, long retention, support bundles, high availability, and generalized plugin systems are optional unless explicitly requested.

# Context Documentation

- Read `context/README.md` and its indexed documents before planning or implementing changes.
- Treat historical product claims and forum posts as evidence, not as current technical truth.
- Keep facts, inferences, and unresolved questions distinct when adding research.
- Record source URLs, archive timestamps when available, retrieval dates, and confidence levels so other models and harnesses can verify the context.
