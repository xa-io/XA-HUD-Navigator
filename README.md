# XA HUD Navigator

XA HUD Navigator is a Dalamud plugin for FFXIV that provides a real-time visual inspector for game UI addons, plus a safe pre-production workspace for debugging addon data paths, Lumina sheet lookups, and live ClientStructs runtime state before moving logic into XA Database or XA Slave. Open the plugin, enable the HUD overlay, and inspect interactive elements, recursive addon paths, schema-aware Excel data, and ClientStructs-backed runtime views without touching production collectors.

- View all our utilities & plugins here: https://aethertek.io/

## Key Features

- **Objects and NPCs** — Inspect classifications, IDs, positions, distances, health, casts and statuses, with search, sortable columns, filters, pinned selection, target shortcuts, a local radar and linked Lumina records.
- **UI Explorer** — Browse supported open shops, selection menus, confirmations and dialogue in readable tables with item IDs, prices, quantities, source fields and expandable raw details.
- **Inventory** — Inspect all four bags, sorted grid pages, armoury categories, crystals and normal/premium saddlebags, including empty slots, container/slot mapping, readable item names and expanded materia, dye, glamour and crafter details.
- **Character** — View all 13 active equipment slots, item condition and attributes, current/max resources, item level, character stats and clearly labeled profile fields.
- **Addon and Node Inspector** — Search loaded addons and browse sortable node trees with native indices, text, bounds, interactive controls, events, component children, TreeList details and texture metadata.
- **HUD Overlay** — Highlight addons and nodes with configurable bounds, IDs, names and interactive-only filtering; overlay alignment follows game UI scale, while inspector panes and labels follow Dalamud zoom.
- **Debugging and Logging** — Capture addon open/close timing, inspect AtkValues and addon/agent ClientStructs fields, and filter or copy recursive node paths and runtime dumps.
- **Lumina Sheets** — Search game-data sheets with schema-aware columns, row navigation, paging, raw/formatted values and copy helpers.
- **CS.Sheets** — Explore ClientStructs-backed player, world, instance, map, quest, social, market and foray data with paging, row filtering and master search across runtime views.
- **Capture and Export** — Objects and UI Explorer each default to stopped, with explicit Start/Stop controls and capture stopping on window close. Retain historical snapshots, copy inspection data and save object/menu JSON.

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
3. Check **Logging** to capture addon open/close timing and `AtkValues` arrays while you interact with the target UI
4. Check **Debugging** to open the recursive debug workspace for the currently selected addon
5. Check **Sheets** to open the combined Lumina / ClientStructs sheet workspace
6. Use **Lumina Sheets** for EXD browsing or **CS.Sheets** for live ClientStructs-backed runtime views
7. In **Lumina Sheets**, use **Row ID**, **Start Index**, **Rows**, and the paging buttons to jump to row IDs or browse visible row windows
8. In **Lumina Sheets**, use **Schema Formatting**, **Offsets**, and **Comments** to switch between raw columns and EXDSchema-style field formatting
9. In **CS.Sheets**, pick a curated runtime view such as `Conditions`, `Weather`, `Public Instance`, `Network Instance`, `Zone Init Packet`, `Contents Finder`, `EventFramework`, `Content Director`, `Dynamic Events`, `Eureka Status`, `Bozja Status`, `Agent Map`, `Map Markers`, `FATE Summary`, `Active FATEs`, `Party Members`, `Inventory Slots`, `Retainers`, `Blacklist`, `Linkshell`, `Cross-world Linkshell`, `Housing`, `Item Search Listings`, or `TargetSystem`
10. For shard and instance research, compare `Public Instance`, `Network Instance`, `Zone Init Packet`, `Eureka Status`, and `Agent Map` together; those views now surface preferred instance candidates, live/replay `ServerId`, replay-cached zone-init values, hook status or unavailable reason, territory filter keys, map marker ranges, and public-content pop-range fields
11. In **CS.Sheets**, compare `FATE Summary` and `Active FATEs` to inspect your current FATE state alongside individual FATE details
12. Use the **Master Search** tab inside **CS.Sheets** to search across all runtime views at once; queries shorter than 3 characters are ignored to reduce noise, and matching results can be opened directly in the runtime preview. Search reads other views before filtering, so a Free Company query also inspects map/event data. Invalid Event Marker storage is skipped, invalid tooltip text is shown as unavailable, and short final pages preserve forward search progress.
13. For runtime views that support it, use **InventoryType**, **Start Index**, **Rows**, the paging buttons, and the loaded-row filter to browse and search live containers and caches in a sheet-style grid
14. Click any grid cell to inspect the selected row, raw/display values, field details, and copy helpers on the right
15. Click any addon in the left panel to inspect its node tree and debug dump
16. Interactive nodes are marked in green — these are buttons, checkboxes, etc that can be clicked via automation
17. In **Debugging**, inspect the selected addon's runtime metadata, including its `GameGui[index]` and `RaptureAtkUnitManager` lookup instances, selected lookup source, generic `AtkUnitBase` snapshot, resolved addon struct, backing agent struct, raw node flags/pointers, searchable `AtkValues`, and previewable cached string vectors, when addon setup state matters more than raw node layout
18. Use **Copy All Nodes**, **Copy Full Dump**, **Copy Filtered Dump**, **Copy Lookup Dump**, **Copy Atk Values**, **Copy Struct Dump**, or the sheet/runtime copy helpers to move verified data into your real plugin code later

## License

[AGPL-3.0-or-later](LICENSE)
