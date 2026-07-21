# RIFT local paths (durable machine facts)

**Last verified:** 2026-07-21  
**Confidence:** High (resolved via `Environment.GetFolderPath(MyDocuments)` and live `rift_x64` process path)

## Binding rule for humans and agents

1. **Always** resolve the user Documents folder with the shell known folder API:
   - C#: `Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)`
   - Do **not** invent `%USERPROFILE%\Documents\...` unless that path is the same as MyDocuments after resolution.
2. On this machine, MyDocuments is **OneDrive-backed**:
   - `C:\Users\mrkoo\OneDrive\Documents`
3. **Player addons load from:**
   - `{MyDocuments}\RIFT\Interface\AddOns\`
   - Canonical example: `C:\Users\mrkoo\OneDrive\Documents\RIFT\Interface\AddOns\`
4. **Before calling an addon deploy “done”:**
   - Confirm the destination directory already contains other player addons (e.g. `JAB`, `ReaderBridge`).
   - If the folder is empty or only contains a freshly created `BotDsBridge`, you are almost certainly on the **wrong** tree.
5. **Wrong paths (do not treat as primary load path):**
   - `C:\Users\mrkoo\Documents\RIFT\Interface\AddOns\` when it is **not** equal to shell MyDocuments (non-redirected / incomplete tree).
   - `C:\Program Files (x86)\Glyph\Games\RIFT\Live\Interface\Addons\` (install tree; may exist but is **not** where this client’s player addons live).
6. **Game executable (for process attach):**
   - `C:\Program Files (x86)\Glyph\Games\RIFT\Live\rift_x64.exe`
   - Process name: `rift_x64`
7. **SavedVariables (if used for debug):**
   - `{MyDocuments}\RIFT\SavedVariables\...` (same MyDocuments root as AddOns).

## BotDsBridge deploy destination

```
{MyDocuments}\RIFT\Interface\AddOns\BotDsBridge\
```

Contents: `RiftAddon.toc`, `main.lua` from repo `addons/BotDsBridge/BotDsBridge/`.

### RiftAddon.toc required fields (or the addon never appears)

RIFT's addon list **silently omits** addons whose TOC lacks non-empty:

| Field | Required |
|-------|----------|
| `Identifier` | non-empty string |
| `Name` | non-empty string |
| `Email` | non-empty string — **`Email = ""` does not count** |

Working addons (JAB, ReaderBridge, RiftMeter, Gadgets) all set a real Email.  
`deploy-addon` validates these three fields before copy.

After deploy: in-game `/reloadui`. UI indicator **BotDs Bridge** top-left when loaded.

## Code entry point

- `BotDs.Core.RiftLocalPaths` — resolves paths; fails closed if MyDocuments is empty.
- `deploy-addon.cmd` — thin wrapper that deploys via C# tool using those paths.

## History of mistakes (so they are not repeated)

| Wrong assumption | Why it failed |
|------------------|---------------|
| Glyph `Live\Interface\Addons` | Install tree; player addons not loaded from here on this setup |
| `%USERPROFILE%\Documents\RIFT\...` without shell resolution | Not the OneDrive MyDocuments folder; nearly empty fake tree |
| “Documents has JAB so it must be right” without listing full AddOns | Incomplete verification; real tree has dozens of addons under OneDrive |
