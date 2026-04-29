# XA HUD Navigator

XA HUD Navigator is a Dalamud plugin for FFXIV that provides a real-time visual inspector for game UI addons, plus a safe pre-production workspace for debugging addon data paths, Lumina sheet lookups, and live ClientStructs runtime state before moving logic into XA Database or XA Slave. Open the plugin, enable the HUD overlay, and inspect interactive elements, recursive addon paths, schema-aware Excel data, and ClientStructs-backed runtime views without touching production collectors.

- View all our utilities & plugins here: https://aethertek.io/

## Key Features

- **Addon List** — Shows all loaded game addons with node counts, visibility, and ready state
- **Node Tree Inspector** — Sortable table of every node in a selected addon: type, position, size, text content, event info
- **Logging Workspace** — Optional logging mode to capture addon appear/close timing while you interact with in-game UI
- **Debug Workspace** — Optional debug tab with recursive component-path dumps, unified filtering across node paths, addon lookup instances, `AtkValues`, and runtime struct fields, selected-branch copy helpers, raw node flags/pointers, a generic `AtkUnitBase` snapshot, and resolved addon/agent ClientStruct field tables for the currently selected addon
- **Lumina Sheets Browser** — Optional sheets workspace with common-sheet shortcuts, all-sheet search, schema-aware column headers, a multi-row grid, row jumping, and copy helpers
- **CS.Sheets Browser** — ClientStructs-backed runtime workspace with curated game, player, world, instance, zone-init packet, map, quest, inventory, retainer, housing, social, market, Free Company, FATE, party, action, target, Eureka, and Bozja views rendered in the same sheet-style grid workflow, plus loaded-row value filtering, replay-cached zone-init correlation, map/public-content range inspection, and a 3-character minimum master search for quick runtime research
- **Interactive Element Detection** — Identifies buttons, checkboxes, sliders, dropdowns, and other interactive component nodes
- **HUD Overlay** — Transparent fullscreen overlay draws colored outlines around visible addons and highlights interactive nodes in green
- **Text Node Display** — Shows text content from AtkTextNode elements for reading addon text
- **Copy to Clipboard** — Copy addon names or full node dumps for use in automation development
- **Search & Filter** — Filter addon list by name, toggle visible-only mode
- **Configurable Overlay** — Toggle bounding boxes, node IDs, addon names, interactive-only mode, alpha, refresh rate
- **Dalamud Interface Scale Support** — Fixed-width inspector panes, sheet grids, and overlay labels follow the current Dalamud interface zoom so the workspace stays usable above or below 100% scale

Selected-node runtime research now includes copyable event-manager details, first-level component child-node summaries, and `TreeList` runtime/renderer sections in the right-side `Node Detail` pane. This is intended for Eureka-style addon work where the important interaction state lives inside a component root rather than a simple text node or top-level callback.

| Command          | Description                             |
| ---------------- | --------------------------------------- |
| `/xahud`         | Toggle the main XA HUD Navigator window |
| `/xahud overlay` | Toggle the HUD overlay on/off           |

## Dependencies

- **Optional:**
  - [XA Database](https://github.com/xa-io/XA-Database) — For Save to XA Database task and IPC data collection
  - [XA Slave](https://github.com/xa-io/XA-Slave) — Handles automation tasks and sends data via IPC

## This Plugin is in Development

This means that there are still features being implemented and enhanced. Suggestions and feature requests are welcome via github issues or by visiting the discord server for direct support.

## This is VERY EARLY ACCESS and should be used with caution

This will likely have game crashes caused by reading too much memory directly from the game. When not in use, you should disable the plugin. Use at your own risk.

## Installation

1. Install [FFXIVQuickLauncher](https://github.com/goatcorp/FFXIVQuickLauncher) and enable Dalamud in its settings. You must run the game through FFXIVQuickLauncher for plugins to work.
2. Open Dalamud settings by typing `/xlsettings` in game chat.
3. Go to the "Experimental" tab.
4. In the "Custom Plugin Repositories" section, paste the following URL:

   ```text
   https://aethertek.io/x.json
   ```

5. Click "Save".
6. Open the plugin installer with `/xlplugins`, go to "All Plugins", and search for **XA HUD Navigator**.

## Support

- Discord server: <https://discord.gg/g2NmYxPQCa>
- Open an issue on the relevant GitHub repository for bugs or feature requests.
- [XA HUD Navigator Issues](https://github.com/xa-io/XA-HUD-Navigator/issues)

## Usage

1. Type `/xahud` in game chat to open the plugin window
2. Check **Overlay** to see addon outlines on the game screen
3. Check **Logging** to capture addon open/close timing while you interact with the target UI
4. Check **Debugging** to open the recursive debug workspace for the currently selected addon
5. Check **Sheets** to open the combined Lumina / ClientStructs sheet workspace
6. Use **Lumina Sheets** for EXD browsing or **CS.Sheets** for live ClientStructs-backed runtime views
7. In **Lumina Sheets**, use **Row ID**, **Start Index**, **Rows**, and the paging buttons to jump to row IDs or browse visible row windows
8. In **Lumina Sheets**, use **Schema Formatting**, **Offsets**, and **Comments** to switch between raw columns and EXDSchema-style field formatting
9. In **CS.Sheets**, pick a curated runtime view such as `Conditions`, `Weather`, `Public Instance`, `Network Instance`, `Zone Init Packet`, `Contents Finder`, `EventFramework`, `Content Director`, `Dynamic Events`, `Eureka Status`, `Bozja Status`, `Agent Map`, `Map Markers`, `FATE Summary`, `Active FATEs`, `Party Members`, `Inventory Slots`, `Retainers`, `Linkshell`, `Cross-world Linkshell`, `Housing`, `Item Search Listings`, or `TargetSystem`
10. For shard and instance research, compare `Public Instance`, `Network Instance`, `Zone Init Packet`, `Eureka Status`, and `Agent Map` together; those views now surface preferred instance candidates, live/replay `ServerId`, replay-cached zone-init values, territory filter keys, map marker ranges, and public-content pop-range fields
11. Use the **Master Search** tab inside **CS.Sheets** to search across all runtime views at once; queries shorter than 3 characters are ignored to reduce noise, and matching results can be opened directly in the runtime preview
12. For runtime views that support it, use **InventoryType**, **Start Index**, **Rows**, the paging buttons, and the loaded-row filter to browse and search live containers and caches in a sheet-style grid
13. Click any grid cell to inspect the selected row, raw/display values, field details, and copy helpers on the right
14. Click any addon in the left panel to inspect its node tree and debug dump
15. Interactive nodes are marked in green — these are buttons, checkboxes, etc that can be clicked via automation
16. In **Debugging**, inspect the selected addon's runtime metadata, including its `GameGui[index]` and `RaptureAtkUnitManager` lookup instances, selected lookup source, generic `AtkUnitBase` snapshot, resolved addon struct, backing agent struct, raw node flags/pointers, searchable `AtkValues`, and previewable cached string vectors, when addon setup state matters more than raw node layout
17. Use **Copy All Nodes**, **Copy Full Dump**, **Copy Filtered Dump**, **Copy Lookup Dump**, **Copy Atk Values**, **Copy Struct Dump**, or the sheet/runtime copy helpers to move verified data into your real plugin code later

## License

[AGPL-3.0-or-later](LICENSE)
