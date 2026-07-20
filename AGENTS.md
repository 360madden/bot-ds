# Repository Status

- The .NET 10 solution contains Core combat/profile contracts, the v5 Reader transport parser, an ASP.NET Core localhost dashboard host, a static dashboard, and xUnit tests.
- Verify with `dotnet build BotDs.sln --no-restore`, `dotnet test BotDs.sln --no-restore`, and `dotnet format BotDs.sln --verify-no-changes --no-restore` after restoring packages.
- The Reader includes Windows x64 process attachment, readable-region enumeration, V5 sentinel scanning, candidate relocation, and scanner metrics. The hosted Reader loop, live addon field population, and action output are not implemented. The application cannot send game input.

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

# Personal Tool Priorities

- Publisher policy, terms-of-service interpretation, account enforcement, and account-risk acceptance are not implementation gates. Do not add recurring warnings, confirmation checkboxes, or approval steps for them.
- Preserve policy references in `context/` only as historical research. Do not repeatedly surface them during implementation unless the user asks.
- Privacy remains binding: keep the application local, do not collect credentials or chat content, avoid sensitive identifiers in logs, and do not expose control endpoints beyond loopback.
- Keep technical controls that prevent unintended input, such as wrong-window checks, stale-state blocking, bounded input rates, cancellation cleanup, and emergency stop. These controls protect local control and privacy rather than publisher accounts.
- Do not add production-service ceremony without a concrete personal-use benefit. Separate audit pipelines, long retention, support bundles, high availability, and generalized plugin systems are optional unless explicitly requested.

# Context Documentation

- Read `context/README.md` and its indexed documents before planning or implementing changes.
- Treat historical product claims and forum posts as evidence, not as current technical truth.
- Keep facts, inferences, and unresolved questions distinct when adding research.
- Record source URLs, archive timestamps when available, retrieval dates, and confidence levels so other models and harnesses can verify the context.
