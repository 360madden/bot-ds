# BotDsBridge

RIFT Lua addon that emits the V5 telemetry double-buffer into process memory for the external C# Reader.

## Install Location

Copy the `BotDsBridge` folder into the RIFT addons directory:

```
<RIFT install>\Interface\Addons\BotDsBridge\
```

The folder must contain `RiftAddon.toc` and `main.lua`. `RiftAddon.toc` declares `main.lua` in its `RunOnStartup` list.

## Current Status

**Provider-only skeleton.** The addon emits the V5 double-buffer envelope with ProtocolVersion=5, CRC32, alternating slots, and a valid sentinel, but all game-state sections (Player, Target, Abilities, Auras) are stubbed and return no data. Every frame is classified as a heartbeat (IsHeartbeat flag set) because only ProviderInfo is populated.

The stubbed TODOs reference `Inspect.Unit.Detail`, `Inspect.Ability.New.Detail`, `Inspect.Buff.Detail`, `Inspect.System.Version`, and `Inspect.System.Secure`. These are ready for implementation once the game client is available for conformance testing.

## Scanner Interoperability Limitation

The external C# Reader locates the region by scanning process memory for the sentinel magic `BotDsV05`. The scanner currently works only with **Windows x64** processes due to:

- Win32 `ReadProcessMemory`/`VirtualQueryEx` usage for readable-region enumeration
- x64-specific address space assumptions (8-byte pointers, 64-bit address ranges)

The more important current limitation is the addon's backing storage. Lua strings are immutable, and `main.lua` rebuilds the logical region during field writes. It therefore does not guarantee a stable virtual address, in-place CRC-last publication, or one physically stable slot. Repeated full-string copies are also too allocation-heavy for live telemetry. The addon remains a provider-envelope prototype until stable mutable storage is validated in the current client or the transport is redesigned.

## Syntax Check

Verify the Lua file parses without errors:

```
luac -p BotDsBridge\main.lua
```

If `luac` is not installed, the file can also be loaded in any Lua 5.1+ interpreter. The file uses no LuaJIT-specific features and is compatible with standard Lua 5.1+.

## Protocol Reference

The authoritative wire protocol specification is in `PROTOCOL.md` (same directory). This document defines every byte offset, size, sentinel, flag, section mask, CRC rule, double-buffer discipline, and torn-read semantic that the addon emitter and the C# Reader must agree on.

- **Protocol version**: 5
- **Document**: [PROTOCOL.md](./PROTOCOL.md)
- **Status**: normative; both sides derive their wire layouts from this single source
