using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using FFXIVClientStructs.FFXIV.Application.Network;
using FFXIVClientStructs.FFXIV.Application.Network.WorkDefinitions;
using FFXIVClientStructs.FFXIV.Client.Enums;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Fate;
using FFXIVClientStructs.FFXIV.Client.Game.Group;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;
using FFXIVClientStructs.FFXIV.Client.Game.Network;
using FFXIVClientStructs.FFXIV.Client.Network;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using InteropGenerator.Runtime;
using Lumina.Excel.Sheets;
using AgentContentsType = FFXIVClientStructs.FFXIV.Client.UI.Agent.ContentsType;
using ClientTerritoryIntendedUse = FFXIVClientStructs.FFXIV.Client.Enums.TerritoryIntendedUse;
using EventContentType = FFXIVClientStructs.FFXIV.Client.Game.Event.ContentType;
using FrameworkSystem = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework;
using InstanceDynamicEvent = FFXIVClientStructs.FFXIV.Client.Game.InstanceContent.DynamicEvent;
using LuminaContentRouletteSheet = Lumina.Excel.Sheets.ContentRoulette;

namespace XAHudNavigator.Services;

public sealed unsafe class ClientStructsSheetService : IDisposable
{
    private readonly ZoneInstanceSnapshotService zoneInstanceSnapshotService = new();

    private static readonly IReadOnlyList<ClientStructsSheetDefinition> SheetDefinitions = new[]
    {
        new ClientStructsSheetDefinition("GameMain", "Game", "Live zone, map, territory, and content state.", "var gm = GameMain.Instance();"),
        new ClientStructsSheetDefinition("PlayerState", "Player", "Current character identity, level, unlock, and weekly state.", "var ps = PlayerState.Instance();"),
        new ClientStructsSheetDefinition("UIState", "Player", "UI-backed runtime state such as item level, buddy timers, and public-instance fields.", "var uiState = UIState.Instance();"),
        new ClientStructsSheetDefinition("Conditions", "State", "Live player condition flags such as combat, duty, travel, and occupancy state.", "var conditions = Conditions.Instance();"),
        new ClientStructsSheetDefinition("Weather", "World", "Current and upcoming weather state for the active territory.", "var weather = WeatherManager.Instance();"),
        new ClientStructsSheetDefinition("Public Instance", "Instances", "UIState.PublicInstance fields used for public-instance routing and zone selection.", "var publicInstance = &UIState.Instance()->PublicInstance;"),
        new ClientStructsSheetDefinition("Network Instance", "Instances", "Framework and NetworkModule instance state for the current zone.", "var proxy = Framework.Instance()->NetworkModuleProxy;"),
        new ClientStructsSheetDefinition("Zone Init Packet", "Instances", "Last captured raw ZoneInit packet values, including packet instance and PopRangeId.", "hooked from UIModule.HandlePacket(..., UIModulePacketType.ZoneInit, packet);"),
        new ClientStructsSheetDefinition("Contents Finder", "Instances", "Duty Finder queue state, penalties, and queue options from UIState/ContentsFinder.", "var contentsFinder = ContentsFinder.Instance();"),
        new ClientStructsSheetDefinition("EventFramework", "Instances", "Active content-director pointers and public-content state from EventFramework.", "var eventFramework = EventFramework.Instance();"),
        new ClientStructsSheetDefinition("Content Director", "Instances", "Detailed active director state for duties and public content, including timers, rewards, and director text.", "var contentDirector = EventFramework.Instance()->GetContentDirector();"),
        new ClientStructsSheetDefinition("Director Todos", "Instances", "Windowed view of active director objective text rows.", "var todos = EventFramework.Instance()->GetContentDirector()->DirectorTodos;", SupportsWindowing: true),
        new ClientStructsSheetDefinition("Dynamic Events", "Instances", "Active dynamic-event rows from public-content runtime state.", "var container = DynamicEventContainer.GetInstance();", SupportsWindowing: true),
        new ClientStructsSheetDefinition("Eureka Status", "Eureka", "Aggregated Eureka runtime state from GameMain, PlayerState, UIState, Inspect, and PublicContentEureka.", "var eurekaDirector = (PublicContentEureka*)EventFramework.GetPublicContentDirectorByType(PublicContentDirectorType.Eureka);"),
        new ClientStructsSheetDefinition("Bozja Status", "Bozja", "Aggregated Bozja runtime state from PublicContentBozja and its dynamic-event container.", "var bozja = PublicContentBozja.GetInstance();"),
        new ClientStructsSheetDefinition("Quest Summary", "Quests", "QuestManager summary counts and timers.", "var qm = QuestManager.Instance();"),
        new ClientStructsSheetDefinition("Active Quests", "Quests", "Accepted normal quests from QuestManager.NormalQuests.", "var quests = QuestManager.Instance()->NormalQuests;", SupportsWindowing: true),
        new ClientStructsSheetDefinition("Daily Quests", "Quests", "Accepted daily quests from QuestManager.DailyQuests.", "var dailies = QuestManager.Instance()->DailyQuests;", SupportsWindowing: true),
        new ClientStructsSheetDefinition("Leves", "Quests", "Accepted leves from QuestManager.LeveQuests.", "var leves = QuestManager.Instance()->LeveQuests;", SupportsWindowing: true),
        new ClientStructsSheetDefinition("Inventory Summary", "Inventory", "Currency and bag state from InventoryManager.", "var im = InventoryManager.Instance();"),
        new ClientStructsSheetDefinition("Inventory Slots", "Inventory", "Windowed browse of a selected InventoryType container.", "var container = InventoryManager.Instance()->GetInventoryContainer(InventoryType.Inventory1);", SupportsWindowing: true, SupportsInventoryType: true),
        new ClientStructsSheetDefinition("Retainers", "Retainers", "Retainer roster and venture summary data from RetainerManager.", "var rm = RetainerManager.Instance();", SupportsWindowing: true),
        new ClientStructsSheetDefinition("Housing", "Housing", "Current and owned-house state from HousingManager.", "var hm = HousingManager.Instance();"),
        new ClientStructsSheetDefinition("Agent Map", "Map/UI", "Current map-agent selection, marker counts, map paths, and open-map context.", "var agentMap = (AgentMap*)AgentModule.Instance()->GetAgentByInternalId(AgentId.Map);"),
        new ClientStructsSheetDefinition("Map Markers", "Map/UI", "Windowed active map-marker rows from AgentMap.", "var markers = ((AgentMap*)AgentModule.Instance()->GetAgentByInternalId(AgentId.Map))->MapMarkers;", SupportsWindowing: true),
        new ClientStructsSheetDefinition("Event Markers", "Map/UI", "Windowed event-marker rows built by FateManager, EventFramework, and map runtime.", "var eventMarkers = ((AgentMap*)AgentModule.Instance()->GetAgentByInternalId(AgentId.Map))->EventMarkers;", SupportsWindowing: true),
        new ClientStructsSheetDefinition("InfoModule", "Social/UI", "InfoModule and InfoProxy availability state.", "var infoModule = InfoModule.Instance();"),
        new ClientStructsSheetDefinition("Linkshell", "Social/UI", "Normal Linkshell slots from InfoProxyLinkshell.", "var linkshell = (InfoProxyLinkshell*)InfoModule.Instance()->GetInfoProxyById(InfoProxyId.Linkshell);", SupportsWindowing: true),
        new ClientStructsSheetDefinition("Cross-world Linkshell", "Social/UI", "Cross-world Linkshell slots from InfoProxyCrossWorldLinkshell.", "var cwls = (InfoProxyCrossWorldLinkshell*)InfoModule.Instance()->GetInfoProxyById(InfoProxyId.CrossWorldLinkshell);", SupportsWindowing: true),
        new ClientStructsSheetDefinition("Item Search Listings", "Market", "General marketboard listings cached in InfoProxyItemSearch.", "var itemSearch = (InfoProxyItemSearch*)InfoModule.Instance()->GetInfoProxyById(InfoProxyId.ItemSearch);", SupportsWindowing: true),
        new ClientStructsSheetDefinition("Retainer Listings", "Market", "Last targeted retainer listings cached in InfoProxyItemSearch.", "var itemSearch = (InfoProxyItemSearch*)InfoModule.Instance()->GetInfoProxyById(InfoProxyId.ItemSearch);", SupportsWindowing: true),
        new ClientStructsSheetDefinition("Player Retainers", "Market", "Player retainer market cache from InfoProxyItemSearch.", "var itemSearch = (InfoProxyItemSearch*)InfoModule.Instance()->GetInfoProxyById(InfoProxyId.ItemSearch);", SupportsWindowing: true),
        new ClientStructsSheetDefinition("Free Company", "Social/UI", "Free Company summary state from InfoProxyFreeCompany.", "var freeCompany = (InfoProxyFreeCompany*)InfoModule.Instance()->GetInfoProxyById(InfoProxyId.FreeCompany);"),
        new ClientStructsSheetDefinition("FATE Summary", "World", "Current FATE manager summary, including synced/current FATE and joined state.", "var fateManager = FateManager.Instance();"),
        new ClientStructsSheetDefinition("Active FATEs", "World", "Windowed active FATE contexts from FateManager.", "var fates = FateManager.Instance()->Fates;", SupportsWindowing: true),
        new ClientStructsSheetDefinition("Party Members", "Social/UI", "Live party/alliance member runtime data from GroupManager.", "var group = GroupManager.Instance()->GetGroup();", SupportsWindowing: true),
        new ClientStructsSheetDefinition("ActionManager", "Automation", "Current cast, queue, and targeting state from ActionManager.", "var am = ActionManager.Instance();"),
        new ClientStructsSheetDefinition("TargetSystem", "Automation", "Current hard/soft/focus target state from TargetSystem.", "var ts = TargetSystem.Instance();")
    };

    public IReadOnlyList<ClientStructsSheetDefinition> Definitions => SheetDefinitions;

    public void Dispose() => zoneInstanceSnapshotService.Dispose();

    public ClientStructsSheetDefinition? GetDefinition(string? name)
        => string.IsNullOrWhiteSpace(name)
            ? null
            : SheetDefinitions.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));

    public ClientStructsSheetSnapshot ReadSheet(string name, ClientStructsSheetRequest request)
    {
        var definition = GetDefinition(name);
        if (definition == null)
            return CreateEmptySnapshot(new ClientStructsSheetDefinition(name, "Unknown", "Unknown runtime sheet.", string.Empty), "Unknown ClientStructs sheet.");

        return definition.Name switch
        {
            "GameMain" => ReadGameMain(definition),
            "PlayerState" => ReadPlayerState(definition),
            "UIState" => ReadUIState(definition),
            "Conditions" => ReadConditions(definition),
            "Weather" => ReadWeather(definition),
            "Public Instance" => ReadPublicInstance(definition),
            "Network Instance" => ReadNetworkInstance(definition),
            "Zone Init Packet" => ReadZoneInitPacket(definition),
            "Contents Finder" => ReadContentsFinder(definition),
            "EventFramework" => ReadEventFramework(definition),
            "Content Director" => ReadContentDirector(definition),
            "Director Todos" => ReadDirectorTodos(definition, request),
            "Dynamic Events" => ReadDynamicEvents(definition, request),
            "Eureka Status" => ReadEurekaStatus(definition),
            "Bozja Status" => ReadBozjaStatus(definition),
            "Quest Summary" => ReadQuestSummary(definition),
            "Active Quests" => ReadActiveQuests(definition, request),
            "Daily Quests" => ReadDailyQuests(definition, request),
            "Leves" => ReadLeves(definition, request),
            "Inventory Summary" => ReadInventorySummary(definition),
            "Inventory Slots" => ReadInventorySlots(definition, request),
            "Retainers" => ReadRetainers(definition, request),
            "Housing" => ReadHousing(definition),
            "Agent Map" => ReadAgentMap(definition),
            "Map Markers" => ReadMapMarkers(definition, request),
            "Event Markers" => ReadEventMarkers(definition, request),
            "InfoModule" => ReadInfoModule(definition),
            "Linkshell" => ReadLinkshell(definition, request),
            "Cross-world Linkshell" => ReadCrossWorldLinkshell(definition, request),
            "Item Search Listings" => ReadItemSearchListings(definition, request),
            "Retainer Listings" => ReadRetainerListings(definition, request),
            "Player Retainers" => ReadPlayerRetainers(definition, request),
            "Free Company" => ReadFreeCompany(definition),
            "FATE Summary" => ReadFateSummary(definition),
            "Active FATEs" => ReadActiveFates(definition, request),
            "Party Members" => ReadPartyMembers(definition, request),
            "ActionManager" => ReadActionManager(definition),
            "TargetSystem" => ReadTargetSystem(definition),
            _ => CreateEmptySnapshot(definition, $"{definition.Name} is not implemented.")
        };
    }

    public List<ClientStructsSearchResult> SearchAllSheets(string filter, ClientStructsSheetRequest request, int maxResults = 300)
    {
        var trimmedFilter = filter?.Trim() ?? string.Empty;
        if (trimmedFilter.Length < 3)
            return new List<ClientStructsSearchResult>();

        var results = new List<ClientStructsSearchResult>();
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        var rowCount = Math.Clamp(request.RowCount <= 0 ? 100 : request.RowCount, 5, 100);

        foreach (var definition in SheetDefinitions)
        {
            AddDefinitionSearchMatches(definition, trimmedFilter, results, seenKeys, maxResults);
            if (results.Count >= maxResults)
                break;

            var startIndex = 0;
            while (true)
            {
                var snapshot = ReadSheet(definition.Name, new ClientStructsSheetRequest
                {
                    StartIndex = startIndex,
                    RowCount = definition.SupportsWindowing ? 100 : rowCount,
                    InventoryType = request.InventoryType
                });

                AddSnapshotSearchMatches(snapshot, trimmedFilter, results, seenKeys, maxResults);
                if (results.Count >= maxResults || !definition.SupportsWindowing)
                    break;

                if (snapshot.Rows.Count == 0)
                    break;

                var nextStartIndex = snapshot.StartIndex + snapshot.Rows.Count;
                if (nextStartIndex >= snapshot.TotalRowCount)
                    break;

                startIndex = nextStartIndex;
            }

            if (results.Count >= maxResults)
                break;
        }

        return results;
    }

    private static void AddDefinitionSearchMatches(ClientStructsSheetDefinition definition, string filter, List<ClientStructsSearchResult> results, HashSet<string> seenKeys, int maxResults)
    {
        AddSearchResultIfMatch(definition.Name, filter, definition, results, seenKeys, maxResults, "Sheet Name", definition.Name, definition.Description);
        AddSearchResultIfMatch(definition.Category, filter, definition, results, seenKeys, maxResults, "Category", definition.Category, definition.Description);
        AddSearchResultIfMatch(definition.Description, filter, definition, results, seenKeys, maxResults, "Description", definition.Description, definition.AccessSnippet);
        AddSearchResultIfMatch(definition.AccessSnippet, filter, definition, results, seenKeys, maxResults, "Access Snippet", definition.AccessSnippet, definition.Description);
    }

    private static void AddSnapshotSearchMatches(ClientStructsSheetSnapshot snapshot, string filter, List<ClientStructsSearchResult> results, HashSet<string> seenKeys, int maxResults)
    {
        AddSearchResultIfMatch(snapshot.Status, filter, snapshot.Definition, results, seenKeys, maxResults, "Status", snapshot.Status, snapshot.Message);
        AddSearchResultIfMatch(snapshot.Message, filter, snapshot.Definition, results, seenKeys, maxResults, "Message", snapshot.Message, snapshot.Status);

        for (var columnIndex = 0; columnIndex < snapshot.Columns.Count && results.Count < maxResults; columnIndex++)
        {
            var column = snapshot.Columns[columnIndex];
            AddSearchResultIfMatch(column.Header, filter, snapshot.Definition, results, seenKeys, maxResults, "Column Header", column.Header, column.Tooltip, columnIndex: column.ColumnIndex, columnHeader: column.Header);
            AddSearchResultIfMatch(column.Descriptor, filter, snapshot.Definition, results, seenKeys, maxResults, "Column Descriptor", column.Descriptor, column.Tooltip, columnIndex: column.ColumnIndex, columnHeader: column.Header);
            AddSearchResultIfMatch(column.Tooltip, filter, snapshot.Definition, results, seenKeys, maxResults, "Column Tooltip", column.Tooltip, column.Descriptor, columnIndex: column.ColumnIndex, columnHeader: column.Header);
        }

        for (var rowIndex = 0; rowIndex < snapshot.Rows.Count && results.Count < maxResults; rowIndex++)
        {
            var row = snapshot.Rows[rowIndex];
            AddSearchResultIfMatch(row.RowIndex.ToString(CultureInfo.InvariantCulture), filter, snapshot.Definition, results, seenKeys, maxResults, "Row Index", row.RowIndex.ToString(CultureInfo.InvariantCulture), $"Row ID {row.RowId}", row.RowIndex, row.RowId);
            AddSearchResultIfMatch(row.RowId.ToString(CultureInfo.InvariantCulture), filter, snapshot.Definition, results, seenKeys, maxResults, "Row ID", row.RowId.ToString(CultureInfo.InvariantCulture), $"Row Index {row.RowIndex}", row.RowIndex, row.RowId);

            for (var cellIndex = 0; cellIndex < row.Cells.Count && cellIndex < snapshot.Columns.Count && results.Count < maxResults; cellIndex++)
            {
                var cell = row.Cells[cellIndex];
                var column = snapshot.Columns[cellIndex];
                AddSearchResultIfMatch(cell.DisplayText, filter, snapshot.Definition, results, seenKeys, maxResults, "Display Value", cell.DisplayText, cell.RawText, row.RowIndex, row.RowId, column.ColumnIndex, column.Header);
                AddSearchResultIfMatch(cell.RawText, filter, snapshot.Definition, results, seenKeys, maxResults, "Raw Value", cell.RawText, cell.DisplayText, row.RowIndex, row.RowId, column.ColumnIndex, column.Header);
            }
        }
    }

    private static void AddSearchResultIfMatch(
        string? source,
        string filter,
        ClientStructsSheetDefinition definition,
        List<ClientStructsSearchResult> results,
        HashSet<string> seenKeys,
        int maxResults,
        string matchSource,
        string displayText,
        string detailText,
        int rowIndex = -1,
        uint rowId = 0,
        int columnIndex = -1,
        string columnHeader = "")
    {
        if (results.Count >= maxResults || string.IsNullOrWhiteSpace(source) || !source.Contains(filter, StringComparison.OrdinalIgnoreCase))
            return;

        var result = new ClientStructsSearchResult
        {
            SheetName = definition.Name,
            Category = definition.Category,
            RowIndex = rowIndex,
            RowId = rowId,
            ColumnIndex = columnIndex,
            ColumnHeader = columnHeader,
            MatchSource = matchSource,
            DisplayText = displayText ?? string.Empty,
            RawText = source ?? string.Empty,
            DetailText = detailText ?? string.Empty
        };

        var key = $"{result.SheetName}|{result.MatchSource}|{result.RowIndex}|{result.RowId}|{result.ColumnIndex}|{result.ColumnHeader}|{result.RawText}";
        if (seenKeys.Add(key))
            results.Add(result);
    }

    private ClientStructsSheetSnapshot ReadGameMain(ClientStructsSheetDefinition definition)
    {
        var gameMain = GameMain.Instance();
        if (gameMain == null)
            return CreateEmptySnapshot(definition, "GameMain.Instance() returned null.");

        return CreateSummarySnapshot(
            definition,
            $"Pointer: {FormatPointer(gameMain)}",
            "Live GameMain singleton snapshot.",
            new[]
            {
                SummaryRow("Pointer", FormatPointer(gameMain), ((nint)gameMain).ToString("X", CultureInfo.InvariantCulture), "Singleton address"),
                SummaryRow("ConnectedToZone", gameMain->ConnectedToZone, gameMain->ConnectedToZone, "True when the client is connected to the active zone"),
                SummaryRow("TerritoryLoadState", gameMain->TerritoryLoadState, gameMain->TerritoryLoadState, "1 = loading, 2 = loaded, 3 = unloading"),
                SummaryRow("CurrentTerritoryTypeId", gameMain->CurrentTerritoryTypeId, gameMain->CurrentTerritoryTypeId, "Current territory type row id"),
                SummaryRow("CurrentTerritory", ResolveTerritoryName(gameMain->CurrentTerritoryTypeId), gameMain->CurrentTerritoryTypeId, "Current territory resolved through Lumina when available"),
                SummaryRow("NextTerritoryTypeId", gameMain->NextTerritoryTypeId, gameMain->NextTerritoryTypeId, "Incoming territory type row id"),
                SummaryRow("NextTerritory", ResolveTerritoryName(gameMain->NextTerritoryTypeId), gameMain->NextTerritoryTypeId, "Incoming territory resolved through Lumina when available"),
                SummaryRow("CurrentMapId", gameMain->CurrentMapId, gameMain->CurrentMapId, "Current map row id"),
                SummaryRow("CurrentTerritoryFilterKey", gameMain->CurrentTerritoryFilterKey, gameMain->CurrentTerritoryFilterKey, "Current territory filter key used by map/layout runtime"),
                SummaryRow("TransitionTerritoryFilterKey", gameMain->TransitionTerritoryFilterKey, gameMain->TransitionTerritoryFilterKey, "Transition territory filter key used while entering the current area"),
                SummaryRow("CurrentContentFinderConditionId", gameMain->CurrentContentFinderConditionId, gameMain->CurrentContentFinderConditionId, "Current duty/content finder row id"),
                SummaryRow("CurrentContentFinderCondition", ResolveContentFinderConditionName(gameMain->CurrentContentFinderConditionId), gameMain->CurrentContentFinderConditionId, "Current duty/content finder resolved through Lumina when available"),
                SummaryRow("CurrentTerritoryIntendedUseId", FormatTerritoryIntendedUse(gameMain->CurrentTerritoryIntendedUseId), (byte)gameMain->CurrentTerritoryIntendedUseId, "Live intended-use enum"),
                SummaryRow("IsEurekaTerritory", gameMain->CurrentTerritoryIntendedUseId == ClientTerritoryIntendedUse.Eureka, gameMain->CurrentTerritoryIntendedUseId == ClientTerritoryIntendedUse.Eureka, "True when the territory intended use is Eureka"),
                SummaryRow("RuntimeSeconds", gameMain->RuntimeSeconds, gameMain->RuntimeSeconds, "Seconds since runtime counter start"),
                SummaryRow("TerritoryTransitionDelay", gameMain->TerritoryTransitionDelay.ToString("F2", CultureInfo.InvariantCulture), gameMain->TerritoryTransitionDelay, "Transition delay in seconds"),
                SummaryRow("IsInInstanceArea()", gameMain->IsInInstanceArea(), gameMain->IsInInstanceArea(), "Instance-area helper result"),
                SummaryRow("IsInPvPArea()", GameMain.IsInPvPArea(), GameMain.IsInPvPArea(), "Static PvP-area helper result"),
                SummaryRow("IsInPvPInstance()", GameMain.IsInPvPInstance(), GameMain.IsInPvPInstance(), "Static PvP-instance helper result"),
                SummaryRow("IsInGPose()", GameMain.IsInGPose(), GameMain.IsInGPose(), "Static GPose helper result"),
                SummaryRow("IsInIdleCam()", GameMain.IsInIdleCam(), GameMain.IsInIdleCam(), "Static idle-camera helper result")
            });
    }

    private ClientStructsSheetSnapshot ReadPlayerState(ClientStructsSheetDefinition definition)
    {
        var playerState = PlayerState.Instance();
        if (playerState == null)
            return CreateEmptySnapshot(definition, "PlayerState.Instance() returned null.");

        return CreateSummarySnapshot(
            definition,
            $"Pointer: {FormatPointer(playerState)}",
            "Live PlayerState singleton snapshot.",
            new[]
            {
                SummaryRow("Pointer", FormatPointer(playerState), ((nint)playerState).ToString("X", CultureInfo.InvariantCulture), "Singleton address"),
                SummaryRow("IsLoaded", playerState->IsLoaded, playerState->IsLoaded, "Whether PlayerState is currently populated"),
                SummaryRow("CharacterName", playerState->CharacterNameString, playerState->CharacterNameString, "Generated fixed-string accessor"),
                SummaryRow("OnlineId", playerState->OnlineIdString, playerState->OnlineIdString, "PSN / Xbox online identifier if present"),
                SummaryRow("EntityId", playerState->EntityId, playerState->EntityId, "Local player entity id"),
                SummaryRow("ContentId", playerState->ContentId, playerState->ContentId, "Local character content id"),
                SummaryRow("CurrentClassJobId", ResolveClassJobName(playerState->CurrentClassJobId), playerState->CurrentClassJobId, "ClassJob row id resolved through Lumina when available"),
                SummaryRow("CurrentLevel", playerState->CurrentLevel, playerState->CurrentLevel, "Current displayed level"),
                SummaryRow("GetClassJobLevel()", playerState->GetClassJobLevel(), playerState->GetClassJobLevel(), "Current or supplied class/job level helper"),
                SummaryRow("SyncedLevel", playerState->SyncedLevel, playerState->SyncedLevel, "Current synced level"),
                SummaryRow("IsLevelSynced", playerState->IsLevelSynced, playerState->IsLevelSynced, "Level sync state"),
                SummaryRow("MaxLevel", playerState->MaxLevel, playerState->MaxLevel, "Maximum supported player level"),
                SummaryRow("GrandCompany", playerState->GrandCompany, playerState->GrandCompany, "Grand Company id"),
                SummaryRow("HomeAetheryteId", playerState->HomeAetheryteId, playerState->HomeAetheryteId, "Home aetheryte row id"),
                SummaryRow("HasPremiumSaddlebag", playerState->HasPremiumSaddlebag, playerState->HasPremiumSaddlebag, "Premium saddlebag entitlement"),
                SummaryRow("NumOwnedMounts", playerState->NumOwnedMounts, playerState->NumOwnedMounts, "Count of unlocked mounts"),
                SummaryRow("CanFly", playerState->CanFly, playerState->CanFly, "Live flying unlock for current zone"),
                SummaryRow("GetContentValue(2)", playerState->GetContentValue(2), playerState->GetContentValue(2), "Eureka effective elemental level when relevant content is loaded"),
                SummaryRow("GetContentValue(3)", playerState->GetContentValue(3), playerState->GetContentValue(3), "Eureka elemental sync flag when relevant content is loaded"),
                SummaryRow("GetContentValue(4)", playerState->GetContentValue(4), playerState->GetContentValue(4), "Eureka current elemental level when relevant content is loaded"),
                SummaryRow("HasWeeklyBingoJournal", playerState->HasWeeklyBingoJournal, playerState->HasWeeklyBingoJournal, "Whether the Wondrous Tails journal is currently held"),
                SummaryRow("WeeklyBingoNumSecondChancePoints", playerState->WeeklyBingoNumSecondChancePoints, playerState->WeeklyBingoNumSecondChancePoints, "Second chance points from Wondrous Tails"),
                SummaryRow("WeeklyBingoExpire", FormatUnixTimestamp(playerState->GetWeeklyBingoExpireUnixTimestamp()), playerState->GetWeeklyBingoExpireUnixTimestamp(), "Wondrous Tails expiration timestamp"),
                SummaryRow("IsMentor()", playerState->IsMentor(), playerState->IsMentor(), "Any mentor status"),
                SummaryRow("IsNovice()", playerState->IsNovice(), playerState->IsNovice(), "New Adventurer / novice state"),
                SummaryRow("IsReturner()", playerState->IsReturner(), playerState->IsReturner(), "Returner state")
            });
    }

    private ClientStructsSheetSnapshot ReadUIState(ClientStructsSheetDefinition definition)
    {
        var uiState = UIState.Instance();
        if (uiState == null)
            return CreateEmptySnapshot(definition, "UIState.Instance() returned null.");

        return CreateSummarySnapshot(
            definition,
            $"Pointer: {FormatPointer(uiState)}",
            "Live UIState singleton snapshot.",
            new[]
            {
                SummaryRow("Pointer", FormatPointer(uiState), ((nint)uiState).ToString("X", CultureInfo.InvariantCulture), "Singleton address"),
                SummaryRow("CurrentItemLevel", uiState->CurrentItemLevel, uiState->CurrentItemLevel, "Character-window item level"),
                SummaryRow("Buddy.CompanionInfo.TimeLeft", uiState->Buddy.CompanionInfo.TimeLeft.ToString("F1", CultureInfo.InvariantCulture), uiState->Buddy.CompanionInfo.TimeLeft, "Companion remaining time in seconds"),
                SummaryRow("NextMapAllowanceTimestamp", FormatUnixTimestamp(uiState->GetNextMapAllowanceTimestamp()), uiState->GetNextMapAllowanceTimestamp(), "Map allowance reset helper"),
                SummaryRow("NextChallengeLogResetTimestamp", FormatUnixTimestamp(uiState->GetNextChallengeLogResetTimestamp()), uiState->GetNextChallengeLogResetTimestamp(), "Challenge log reset helper"),
                SummaryRow("UnlockedTripleTriadCardsCount", uiState->UnlockedTripleTriadCardsCount, uiState->UnlockedTripleTriadCardsCount, "Total unlocked Triple Triad cards count"),
                SummaryRow("UnlockedCompanionsCount", uiState->UnlockedCompanionsCount, uiState->UnlockedCompanionsCount, "Total unlocked companion count"),
                SummaryRow("TerritoryTypeTransientRowLoaded", uiState->TerritoryTypeTransientRowLoaded, uiState->TerritoryTypeTransientRowLoaded, "Whether the transient territory row is loaded"),
                SummaryRow("PublicInstance.AddonId", uiState->PublicInstance.AddonId, uiState->PublicInstance.AddonId, "SelectString addon id used for public-instance selection"),
                SummaryRow("PublicInstance.CloseCountdown", uiState->PublicInstance.CloseCountdown.ToString("F1", CultureInfo.InvariantCulture), uiState->PublicInstance.CloseCountdown, "Countdown while the public-instance selection window is open"),
                SummaryRow("PublicInstance.TerritoryTypeId", ResolveTerritoryName(uiState->PublicInstance.TerritoryTypeId), uiState->PublicInstance.TerritoryTypeId, "Territory attached to the current public-instance selection state"),
                SummaryRow("PublicInstance.InstanceId", uiState->PublicInstance.InstanceId, uiState->PublicInstance.InstanceId, "Current public-instance id exposed by UIState"),
                SummaryRow("PublicInstance.IsInstancedArea()", uiState->PublicInstance.IsInstancedArea(), uiState->PublicInstance.IsInstancedArea(), "True when UIState believes the current area is publicly instanced"),
                SummaryRow("GMRank", uiState->GMRank, uiState->GMRank, "GM rank / debug state byte")
            });
    }

    private ClientStructsSheetSnapshot ReadConditions(ClientStructsSheetDefinition definition)
    {
        var conditions = Conditions.Instance();
        if (conditions == null)
            return CreateEmptySnapshot(definition, "Conditions.Instance() returned null.");

        return CreateSummarySnapshot(
            definition,
            $"Pointer: {FormatPointer(conditions)}",
            "Live player-condition snapshot.",
            new[]
            {
                SummaryRow("Pointer", FormatPointer(conditions), ((nint)conditions).ToString("X", CultureInfo.InvariantCulture), "Conditions singleton address"),
                SummaryRow("Normal", conditions->Normal, conditions->Normal, "True during normal free-movement state"),
                SummaryRow("Occupied", conditions->Occupied, conditions->Occupied, "Generic occupied-state flag"),
                SummaryRow("InCombat", conditions->InCombat, conditions->InCombat, "Combat state"),
                SummaryRow("Casting", conditions->Casting, conditions->Casting, "Castbar / casting state"),
                SummaryRow("Mounted", conditions->Mounted, conditions->Mounted, "Mounted state"),
                SummaryRow("Mounting", conditions->Mounting, conditions->Mounting, "Mount animation transition"),
                SummaryRow("Crafting", conditions->Crafting, conditions->Crafting, "Crafting mode state"),
                SummaryRow("Gathering", conditions->Gathering, conditions->Gathering, "Gathering mode state"),
                SummaryRow("Fishing", conditions->Fishing, conditions->Fishing, "Fishing state"),
                SummaryRow("BoundByDuty", conditions->BoundByDuty || conditions->BoundByDuty56 || conditions->BoundByDuty95, conditions->BoundByDuty || conditions->BoundByDuty56 || conditions->BoundByDuty95, "Any duty-bind style flag observed on Conditions"),
                SummaryRow("BetweenAreas", conditions->BetweenAreas || conditions->BetweenAreas51, conditions->BetweenAreas || conditions->BetweenAreas51, "Zone transition state"),
                SummaryRow("WatchingCutscene", conditions->WatchingCutscene || conditions->WatchingCutscene78, conditions->WatchingCutscene || conditions->WatchingCutscene78, "Any cutscene-watching flag"),
                SummaryRow("WaitingForDutyFinder", conditions->WaitingForDutyFinder, conditions->WaitingForDutyFinder, "Queued / waiting for duty finder"),
                SummaryRow("InDutyQueue", conditions->InDutyQueue, conditions->InDutyQueue, "Duty queue state"),
                SummaryRow("InFlight", conditions->InFlight, conditions->InFlight, "Flying state"),
                SummaryRow("Swimming", conditions->Swimming, conditions->Swimming, "Swimming state"),
                SummaryRow("Diving", conditions->Diving, conditions->Diving, "Diving state"),
                SummaryRow("UsingHousingFunctions", conditions->UsingHousingFunctions, conditions->UsingHousingFunctions, "Housing interaction state"),
                SummaryRow("ParticipatingInCrossWorldPartyOrAlliance", conditions->ParticipatingInCrossWorldPartyOrAlliance, conditions->ParticipatingInCrossWorldPartyOrAlliance, "Cross-world party/alliance state"),
                SummaryRow("InDeepDungeon", conditions->InDeepDungeon, conditions->InDeepDungeon, "Deep Dungeon state"),
                SummaryRow("PilotingMech", conditions->PilotingMech, conditions->PilotingMech, "Cosmic Exploration mech state"),
                SummaryRow("MountOrOrnamentTransitionResetTimer", conditions->MountOrOrnamentTransitionResetTimer.ToString("F2", CultureInfo.InvariantCulture), conditions->MountOrOrnamentTransitionResetTimer, "Timer that clears the mount/ornament transition flag")
            });
    }

    private ClientStructsSheetSnapshot ReadWeather(ClientStructsSheetDefinition definition)
    {
        var weatherManager = WeatherManager.Instance();
        if (weatherManager == null)
            return CreateEmptySnapshot(definition, "WeatherManager.Instance() returned null.");

        var gameMain = GameMain.Instance();
        var territoryId = (ushort)(gameMain == null ? 0 : gameMain->CurrentTerritoryTypeId);
        var currentWeather = weatherManager->GetCurrentWeather();
        var nextWeather = territoryId == 0 ? (byte)0 : weatherManager->GetWeatherForDaytime(territoryId, 1);
        var afterNextWeather = territoryId == 0 ? (byte)0 : weatherManager->GetWeatherForDaytime(territoryId, 2);

        return CreateSummarySnapshot(
            definition,
            $"Pointer: {FormatPointer(weatherManager)}",
            "WeatherManager snapshot for current and upcoming territory weather.",
            new[]
            {
                SummaryRow("Pointer", FormatPointer(weatherManager), ((nint)weatherManager).ToString("X", CultureInfo.InvariantCulture), "WeatherManager singleton address"),
                SummaryRow("CurrentTerritory", territoryId == 0 ? "0" : ResolveTerritoryName(territoryId), territoryId, "Territory used for the weather lookup"),
                SummaryRow("WeatherIndex", weatherManager->WeatherIndex, weatherManager->WeatherIndex, "Current weather slot index"),
                SummaryRow("WeatherId", ResolveWeatherName(weatherManager->WeatherId), weatherManager->WeatherId, "Current weather id field"),
                SummaryRow("GetCurrentWeather()", ResolveWeatherName(currentWeather), currentWeather, "Resolved current weather helper"),
                SummaryRow("WeatherOverride", ResolveWeatherName(weatherManager->WeatherOverride), weatherManager->WeatherOverride, "Weather override id when active"),
                SummaryRow("IndividualWeatherId", ResolveWeatherName(weatherManager->IndividualWeatherId), weatherManager->IndividualWeatherId, "Individual weather id for territories that support it"),
                SummaryRow("CurrentDaytimeOffset", weatherManager->CurrentDaytimeOffset, weatherManager->CurrentDaytimeOffset, "0-2 daytime block inside the 24h weather cycle"),
                SummaryRow("HasIndividualWeather", territoryId != 0 && weatherManager->HasIndividualWeather(territoryId), territoryId != 0 && weatherManager->HasIndividualWeather(territoryId), "Whether the active territory uses individual weather"),
                SummaryRow("GetWeatherForDaytime(+1)", territoryId == 0 ? "0" : ResolveWeatherName(nextWeather), nextWeather, "Weather expected after the next weather change"),
                SummaryRow("GetWeatherForDaytime(+2)", territoryId == 0 ? "0" : ResolveWeatherName(afterNextWeather), afterNextWeather, "Weather expected two blocks ahead"),
                SummaryRow("ServerWeather.Current", ResolveWeatherName(weatherManager->Weathers[weatherManager->WeatherIndex].CurrentWeatherId), weatherManager->Weathers[weatherManager->WeatherIndex].CurrentWeatherId, "Current weather from the backing server-weather slot"),
                SummaryRow("ServerWeather.Next", ResolveWeatherName(weatherManager->Weathers[weatherManager->WeatherIndex].NextWeatherId), weatherManager->Weathers[weatherManager->WeatherIndex].NextWeatherId, "Next weather from the backing server-weather slot"),
                SummaryRow("DaytimeFadeTimeLeft", weatherManager->Weathers[weatherManager->WeatherIndex].DaytimeFadeTimeLeft.ToString("F2", CultureInfo.InvariantCulture), weatherManager->Weathers[weatherManager->WeatherIndex].DaytimeFadeTimeLeft, "Fade time remaining in the active server-weather slot"),
                SummaryRow("DaytimeFadeLength", weatherManager->Weathers[weatherManager->WeatherIndex].DaytimeFadeLength.ToString("F2", CultureInfo.InvariantCulture), weatherManager->Weathers[weatherManager->WeatherIndex].DaytimeFadeLength, "Fade duration in the active server-weather slot")
            });
    }

    private ClientStructsSheetSnapshot ReadPublicInstance(ClientStructsSheetDefinition definition)
    {
        var uiState = UIState.Instance();
        if (uiState == null)
            return CreateEmptySnapshot(definition, "UIState.Instance() returned null.");

        var publicInstance = &uiState->PublicInstance;
        var framework = FrameworkSystem.Instance();
        var proxy = framework == null ? null : framework->NetworkModuleProxy;
        var networkModule = proxy == null ? null : proxy->NetworkModule;
        var proxyCurrentInstance = proxy == null ? (short)0 : proxy->GetCurrentInstance();
        var networkCurrentInstance = networkModule == null ? (short)0 : networkModule->CurrentInstance;
        var gameMain = GameMain.Instance();
        var zoneInit = zoneInstanceSnapshotService.GetSnapshot();
        var replayManager = ContentsReplayManager.Instance();
        var hasReplayZoneInit = replayManager != null && replayManager->ZoneInitPacket.TerritoryTypeId != 0;
        var agentMap = TryGetAgentMap();
        var eventFramework = EventFramework.Instance();
        var publicContentDirector = eventFramework == null ? null : eventFramework->GetPublicContentDirector();
        var preferredInstanceCandidate = ResolvePreferredInstanceCandidate(
            publicInstance->InstanceId,
            proxyCurrentInstance,
            networkCurrentInstance,
            Plugin.ClientState.Instance,
            zoneInit,
            replayManager,
            hasReplayZoneInit,
            out var preferredInstanceSource);
        var preferredServerIdCandidate = ResolvePreferredServerIdCandidate(zoneInit, replayManager, hasReplayZoneInit, out var preferredServerIdSource);
        var preferredPopRangeCandidate = ResolvePreferredPopRangeCandidate(zoneInit, replayManager, hasReplayZoneInit, publicContentDirector, out var preferredPopRangeSource);

        return CreateSummarySnapshot(
            definition,
            $"Pointer: {FormatPointer(publicInstance)}",
            "Live UIState.PublicInstance snapshot.",
            new[]
            {
                SummaryRow("Pointer", FormatPointer(publicInstance), ((nint)publicInstance).ToString("X", CultureInfo.InvariantCulture), "PublicInstance address inside UIState"),
                SummaryRow("AddonId", publicInstance->AddonId, publicInstance->AddonId, "SelectString addon id that owns the current public-instance prompt"),
                SummaryRow("CloseCountdown", publicInstance->CloseCountdown.ToString("F1", CultureInfo.InvariantCulture), publicInstance->CloseCountdown, "Countdown while the public-instance selection window is open"),
                SummaryRow("TerritoryTypeId", ResolveTerritoryName(publicInstance->TerritoryTypeId), publicInstance->TerritoryTypeId, "Territory associated with the current public-instance state"),
                SummaryRow("InstanceId", publicInstance->InstanceId, publicInstance->InstanceId, "Public-instance id currently exposed by UIState"),
                SummaryRow("PreferredInstanceCandidate", preferredInstanceCandidate == 0 ? "<none>" : preferredInstanceCandidate, preferredInstanceCandidate, "First non-zero candidate from UIState.PublicInstance.InstanceId -> NetworkModuleProxy.GetCurrentInstance() -> NetworkModule.CurrentInstance -> Dalamud.ClientState.Instance -> LastZoneInit.PacketInstance -> ReplayZoneInit.Instance"),
                SummaryRow("PreferredInstanceSource", preferredInstanceSource, preferredInstanceSource, "Source currently supplying PreferredInstanceCandidate"),
                SummaryRow("PreferredServerIdCandidate", preferredServerIdCandidate == 0 ? "<none>" : preferredServerIdCandidate, preferredServerIdCandidate, "First non-zero server-id candidate from LastZoneInit.ServerId -> ReplayZoneInit.ServerId"),
                SummaryRow("PreferredServerIdSource", preferredServerIdSource, preferredServerIdSource, "Source currently supplying PreferredServerIdCandidate"),
                SummaryRow("PreferredPopRangeCandidate", preferredPopRangeCandidate == 0 ? "<none>" : preferredPopRangeCandidate, preferredPopRangeCandidate, "First non-zero layout/pop-range candidate from LastZoneInit.PopRangeId -> ReplayZoneInit.PopRangeId -> PublicContent.LGBPopRange"),
                SummaryRow("PreferredPopRangeSource", preferredPopRangeSource, preferredPopRangeSource, "Source currently supplying PreferredPopRangeCandidate"),
                SummaryRow("NetworkModuleProxy.GetCurrentInstance()", proxyCurrentInstance, proxyCurrentInstance, "Current instance returned by NetworkModuleProxy"),
                SummaryRow("NetworkModule.CurrentInstance", networkCurrentInstance, networkCurrentInstance, "Backing short field stored in NetworkModule"),
                SummaryRow("Dalamud.ClientState.Instance", Plugin.ClientState.Instance, Plugin.ClientState.Instance, "Dalamud client-state instance maintained from NetworkModuleProxy.SetCurrentInstance"),
                SummaryRow("LastZoneInit.ServerId", zoneInit.HasCapturedPacket ? zoneInit.ServerId : "<unavailable>", zoneInit.HasCapturedPacket ? zoneInit.ServerId : 0, "Server id from the last captured ZoneInit packet; old EurekaHelper-style shard research should compare this against instance and pop-range data"),
                SummaryRow("LastZoneInit.PacketInstance", zoneInit.HasCapturedPacket ? zoneInit.PacketInstance : "<unavailable>", zoneInit.HasCapturedPacket ? zoneInit.PacketInstance : 0, "Packet `Instance` from the last captured ZoneInit packet"),
                SummaryRow("LastZoneInit.PopRangeId", zoneInit.HasCapturedPacket ? zoneInit.PopRangeId : "<unavailable>", zoneInit.HasCapturedPacket ? zoneInit.PopRangeId : 0, "Packet `PopRangeId` from the last captured ZoneInit packet; ClientStructs notes this as the PlanMap instance id"),
                SummaryRow("ReplayZoneInit.ServerId", hasReplayZoneInit ? replayManager->ZoneInitPacket.ServerId : "<unavailable>", hasReplayZoneInit ? replayManager->ZoneInitPacket.ServerId : 0, "Server id from the ZoneInitPacket cached on ContentsReplayManager"),
                SummaryRow("ReplayZoneInit.Instance", hasReplayZoneInit ? replayManager->ZoneInitPacket.Instance : "<unavailable>", hasReplayZoneInit ? replayManager->ZoneInitPacket.Instance : 0, "ZoneInitPacket.Instance cached on ContentsReplayManager; useful when HUD Navigator loaded after zone-in"),
                SummaryRow("ReplayZoneInit.PopRangeId", hasReplayZoneInit ? replayManager->ZoneInitPacket.PopRangeId : "<unavailable>", hasReplayZoneInit ? replayManager->ZoneInitPacket.PopRangeId : 0, "ZoneInitPacket.PopRangeId cached on ContentsReplayManager"),
                SummaryRow("GameMain.CurrentTerritoryFilterKey", gameMain == null ? 0 : gameMain->CurrentTerritoryFilterKey, gameMain == null ? 0 : gameMain->CurrentTerritoryFilterKey, "Current territory filter key from GameMain"),
                SummaryRow("GameMain.TransitionTerritoryFilterKey", gameMain == null ? 0 : gameMain->TransitionTerritoryFilterKey, gameMain == null ? 0 : gameMain->TransitionTerritoryFilterKey, "Transition territory filter key from GameMain"),
                SummaryRow("AgentMap.CurrentMapMarkerRange", agentMap == null ? 0 : agentMap->CurrentMapMarkerRange, agentMap == null ? 0 : agentMap->CurrentMapMarkerRange, "Current map marker range from AgentMap"),
                SummaryRow("AgentMap.SelectedMapMarkerRange", agentMap == null ? 0 : agentMap->SelectedMapMarkerRange, agentMap == null ? 0 : agentMap->SelectedMapMarkerRange, "Selected map marker range from AgentMap"),
                SummaryRow("PublicContent.LGBPopRange", publicContentDirector == null ? 0 : publicContentDirector->LGBPopRange, publicContentDirector == null ? 0 : publicContentDirector->LGBPopRange, "Active public-content pop-range field"),
                SummaryRow("IsInstancedArea()", publicInstance->IsInstancedArea(), publicInstance->IsInstancedArea(), "True when PublicInstance.InstanceId is non-zero"),
                SummaryRow("MatchesGameMainTerritory", gameMain != null && publicInstance->TerritoryTypeId == gameMain->CurrentTerritoryTypeId, gameMain != null && publicInstance->TerritoryTypeId == gameMain->CurrentTerritoryTypeId, "Whether PublicInstance.TerritoryTypeId matches GameMain.CurrentTerritoryTypeId")
            });
    }

    private ClientStructsSheetSnapshot ReadNetworkInstance(ClientStructsSheetDefinition definition)
    {
        var framework = FrameworkSystem.Instance();
        if (framework == null)
            return CreateEmptySnapshot(definition, "Framework.Instance() returned null.");

        var proxy = framework->NetworkModuleProxy;
        var networkModule = proxy == null ? null : proxy->NetworkModule;
        var proxyCurrentInstance = proxy == null ? (short)0 : proxy->GetCurrentInstance();
        var proxyCrossWorldDuty = proxy != null && proxy->IsInCrossWorldDuty();
        var zoneInit = zoneInstanceSnapshotService.GetSnapshot();
        var replayManager = ContentsReplayManager.Instance();
        var hasReplayZoneInit = replayManager != null && replayManager->ZoneInitPacket.TerritoryTypeId != 0;
        var eventFramework = EventFramework.Instance();
        var publicContentDirector = eventFramework == null ? null : eventFramework->GetPublicContentDirector();
        var preferredInstanceCandidate = ResolvePreferredInstanceCandidate(
            0,
            proxyCurrentInstance,
            networkModule == null ? (short)0 : networkModule->CurrentInstance,
            Plugin.ClientState.Instance,
            zoneInit,
            replayManager,
            hasReplayZoneInit,
            out var preferredInstanceSource);
        var preferredServerIdCandidate = ResolvePreferredServerIdCandidate(zoneInit, replayManager, hasReplayZoneInit, out var preferredServerIdSource);
        var preferredPopRangeCandidate = ResolvePreferredPopRangeCandidate(zoneInit, replayManager, hasReplayZoneInit, publicContentDirector, out var preferredPopRangeSource);

        return CreateSummarySnapshot(
            definition,
            $"Framework: {FormatPointer(framework)}",
            "Framework / NetworkModule instance state snapshot.",
            new[]
            {
                SummaryRow("Framework", FormatPointer(framework), ((nint)framework).ToString("X", CultureInfo.InvariantCulture), "Framework singleton address"),
                SummaryRow("NetworkModuleProxy", FormatPointer(proxy), proxy == null ? "0" : ((nint)proxy).ToString("X", CultureInfo.InvariantCulture), "Framework-owned NetworkModuleProxy"),
                SummaryRow("NetworkModule", FormatPointer(networkModule), networkModule == null ? "0" : ((nint)networkModule).ToString("X", CultureInfo.InvariantCulture), "Backed Application::Network::NetworkModule"),
                SummaryRow("IsNetworkModuleInitialized", framework->IsNetworkModuleInitialized, framework->IsNetworkModuleInitialized, "Framework-side network module initialization state"),
                SummaryRow("EnableNetworking", framework->EnableNetworking, framework->EnableNetworking, "Framework-side networking enable flag"),
                SummaryRow("Proxy.GetCurrentInstance()", proxyCurrentInstance, proxyCurrentInstance, "Current instance returned by NetworkModuleProxy"),
                SummaryRow("NetworkModule.CurrentInstance", networkModule == null ? 0 : networkModule->CurrentInstance, networkModule == null ? 0 : networkModule->CurrentInstance, "Backing short field stored in NetworkModule"),
                SummaryRow("PreferredInstanceCandidate", preferredInstanceCandidate == 0 ? "<none>" : preferredInstanceCandidate, preferredInstanceCandidate, "First non-zero candidate from NetworkModuleProxy.GetCurrentInstance() -> NetworkModule.CurrentInstance -> Dalamud.ClientState.Instance -> LastZoneInit.PacketInstance -> ReplayZoneInit.Instance"),
                SummaryRow("PreferredInstanceSource", preferredInstanceSource, preferredInstanceSource, "Source currently supplying PreferredInstanceCandidate"),
                SummaryRow("PreferredServerIdCandidate", preferredServerIdCandidate == 0 ? "<none>" : preferredServerIdCandidate, preferredServerIdCandidate, "First non-zero server-id candidate from LastZoneInit.ServerId -> ReplayZoneInit.ServerId"),
                SummaryRow("PreferredServerIdSource", preferredServerIdSource, preferredServerIdSource, "Source currently supplying PreferredServerIdCandidate"),
                SummaryRow("PreferredPopRangeCandidate", preferredPopRangeCandidate == 0 ? "<none>" : preferredPopRangeCandidate, preferredPopRangeCandidate, "First non-zero layout/pop-range candidate from LastZoneInit.PopRangeId -> ReplayZoneInit.PopRangeId -> PublicContent.LGBPopRange"),
                SummaryRow("PreferredPopRangeSource", preferredPopRangeSource, preferredPopRangeSource, "Source currently supplying PreferredPopRangeCandidate"),
                SummaryRow("Dalamud.ClientState.Instance", Plugin.ClientState.Instance, Plugin.ClientState.Instance, "Dalamud client-state instance maintained from NetworkModuleProxy.SetCurrentInstance"),
                SummaryRow("LastZoneInit.ServerId", zoneInit.HasCapturedPacket ? zoneInit.ServerId : "<unavailable>", zoneInit.HasCapturedPacket ? zoneInit.ServerId : 0, "Server id from the last captured ZoneInit packet"),
                SummaryRow("LastZoneInit.PacketInstance", zoneInit.HasCapturedPacket ? zoneInit.PacketInstance : "<unavailable>", zoneInit.HasCapturedPacket ? zoneInit.PacketInstance : 0, "Packet `Instance` from the last captured ZoneInit packet"),
                SummaryRow("LastZoneInit.PopRangeId", zoneInit.HasCapturedPacket ? zoneInit.PopRangeId : "<unavailable>", zoneInit.HasCapturedPacket ? zoneInit.PopRangeId : 0, "Packet `PopRangeId` from the last captured ZoneInit packet; ClientStructs notes this as the PlanMap instance id"),
                SummaryRow("ReplayZoneInit.ServerId", hasReplayZoneInit ? replayManager->ZoneInitPacket.ServerId : "<unavailable>", hasReplayZoneInit ? replayManager->ZoneInitPacket.ServerId : 0, "Server id from the ZoneInitPacket cached on ContentsReplayManager"),
                SummaryRow("ReplayZoneInit.Instance", hasReplayZoneInit ? replayManager->ZoneInitPacket.Instance : "<unavailable>", hasReplayZoneInit ? replayManager->ZoneInitPacket.Instance : 0, "ZoneInitPacket.Instance cached on ContentsReplayManager"),
                SummaryRow("ReplayZoneInit.PopRangeId", hasReplayZoneInit ? replayManager->ZoneInitPacket.PopRangeId : "<unavailable>", hasReplayZoneInit ? replayManager->ZoneInitPacket.PopRangeId : 0, "PopRangeId from the cached ZoneInitPacket"),
                SummaryRow("Proxy.IsInCrossWorldDuty()", proxyCrossWorldDuty, proxyCrossWorldDuty, "Current cross-world duty helper result"),
                SummaryRow("NetworkModule.IsInCrossWorldDuty", networkModule != null && networkModule->IsInCrossWorldDuty, networkModule != null && networkModule->IsInCrossWorldDuty, "Backing cross-world duty flag on NetworkModule"),
                SummaryRow("NetworkModule.World", networkModule == null ? string.Empty : networkModule->World.ToString(), networkModule == null ? string.Empty : networkModule->World.ToString(), "World string cached by NetworkModule"),
                SummaryRow("NetworkModule.ZoneName", networkModule == null ? string.Empty : networkModule->ZoneName.ToString(), networkModule == null ? string.Empty : networkModule->ZoneName.ToString(), "Zone-name string cached by NetworkModule"),
                SummaryRow("FrontHost", networkModule == null ? string.Empty : networkModule->FrontHost.ToString(), networkModule == null ? string.Empty : networkModule->FrontHost.ToString(), "Current frontend host"),
                SummaryRow("FrontPort", networkModule == null ? 0 : networkModule->FrontPort, networkModule == null ? 0 : networkModule->FrontPort, "Current frontend port"),
                SummaryRow("LobbyPing", networkModule == null ? 0 : networkModule->LobbyPing, networkModule == null ? 0 : networkModule->LobbyPing, "Current lobby ping reported by NetworkModule"),
                SummaryRow("CurrentDeviceTime", FormatUnixTimestamp(networkModule == null ? 0 : networkModule->CurrentDeviceTime), networkModule == null ? 0 : networkModule->CurrentDeviceTime, "Network-module device time timestamp"),
                SummaryRow("KeepAliveZone", networkModule == null ? 0 : networkModule->KeepAliveZone, networkModule == null ? 0 : networkModule->KeepAliveZone, "Zone keepalive timestamp/value"),
                SummaryRow("KeepAliveIntervalZone", networkModule == null ? 0 : networkModule->KeepAliveIntervalZone, networkModule == null ? 0 : networkModule->KeepAliveIntervalZone, "Zone keepalive interval"),
                SummaryRow("KeepAliveChat", networkModule == null ? 0 : networkModule->KeepAliveChat, networkModule == null ? 0 : networkModule->KeepAliveChat, "Chat keepalive timestamp/value"),
                SummaryRow("KeepAliveIntervalChat", networkModule == null ? 0 : networkModule->KeepAliveIntervalChat, networkModule == null ? 0 : networkModule->KeepAliveIntervalChat, "Chat keepalive interval")
            });
    }

    private ClientStructsSheetSnapshot ReadZoneInitPacket(ClientStructsSheetDefinition definition)
    {
        var snapshot = zoneInstanceSnapshotService.GetSnapshot();
        var replayManager = ContentsReplayManager.Instance();
        var hasReplayZoneInit = replayManager != null && replayManager->ZoneInitPacket.TerritoryTypeId != 0;
        var gameMain = GameMain.Instance();
        var agentMap = TryGetAgentMap();
        var eventFramework = EventFramework.Instance();
        var publicContentDirector = eventFramework == null ? null : eventFramework->GetPublicContentDirector();

        return CreateSummarySnapshot(
            definition,
            snapshot.HasCapturedPacket
                ? $"Captured: {snapshot.CapturedAtUtc:yyyy-MM-dd HH:mm:ss} UTC"
                : "No ZoneInit packet captured yet.",
            "Last raw ZoneInit packet observed by HUD Navigator.",
            new[]
            {
                SummaryRow("HookActive", snapshot.HookActive, snapshot.HookActive, "Whether the plugin-level UIModule.HandlePacket hook for ZoneInit is active"),
                SummaryRow("HasCapturedPacket", snapshot.HasCapturedPacket, snapshot.HasCapturedPacket, "Whether a ZoneInit packet has been seen since the plugin loaded"),
                SummaryRow("CapturedAtUtc", snapshot.HasCapturedPacket ? snapshot.CapturedAtUtc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : "<unavailable>", snapshot.HasCapturedPacket ? snapshot.CapturedAtUtc.ToString("O", CultureInfo.InvariantCulture) : string.Empty, "UTC timestamp of the last captured ZoneInit packet"),
                SummaryRow("Dalamud.ClientState.Instance", snapshot.DalamudClientStateInstance, snapshot.DalamudClientStateInstance, "Dalamud client-state instance maintained from NetworkModuleProxy.SetCurrentInstance"),
                SummaryRow("ServerId", snapshot.HasCapturedPacket ? snapshot.ServerId : "<unavailable>", snapshot.HasCapturedPacket ? snapshot.ServerId : 0, "Server id from the last ZoneInit packet"),
                SummaryRow("TerritoryTypeId", snapshot.HasCapturedPacket ? ResolveTerritoryName(snapshot.TerritoryTypeId) : "<unavailable>", snapshot.HasCapturedPacket ? snapshot.TerritoryTypeId : 0, "Territory id from the last ZoneInit packet"),
                SummaryRow("Packet.Instance", snapshot.HasCapturedPacket ? snapshot.PacketInstance : "<unavailable>", snapshot.HasCapturedPacket ? snapshot.PacketInstance : 0, "Packet `Instance`; only meaningful when the zone-init flags say the area is instanced"),
                SummaryRow("Packet.ContentFinderConditionId", snapshot.HasCapturedPacket ? ResolveContentFinderConditionName(snapshot.ContentFinderConditionId) : "<unavailable>", snapshot.HasCapturedPacket ? snapshot.ContentFinderConditionId : 0, "ContentFinderCondition id from the last ZoneInit packet"),
                SummaryRow("Packet.TransitionTerritoryFilterKey", snapshot.HasCapturedPacket ? snapshot.TransitionTerritoryFilterKey : "<unavailable>", snapshot.HasCapturedPacket ? snapshot.TransitionTerritoryFilterKey : 0, "Transition territory filter key from the last ZoneInit packet"),
                SummaryRow("Packet.PopRangeId", snapshot.HasCapturedPacket ? snapshot.PopRangeId : "<unavailable>", snapshot.HasCapturedPacket ? snapshot.PopRangeId : 0, "Packet `PopRangeId`; ClientStructs documents this as the PlanMap instance id"),
                SummaryRow("Packet.WeatherId", snapshot.HasCapturedPacket ? ResolveWeatherName(snapshot.WeatherId) : "<unavailable>", snapshot.HasCapturedPacket ? snapshot.WeatherId : 0, "Weather id from the last ZoneInit packet"),
                SummaryRow("Packet.Flags", snapshot.HasCapturedPacket ? FormatZoneInitFlags(snapshot.Flags) : "<unavailable>", snapshot.HasCapturedPacket ? (ushort)snapshot.Flags : 0, "Zone-init flags bitfield from the last ZoneInit packet"),
                SummaryRow("Packet.Flags.IsInstancedArea", snapshot.HasCapturedPacket && snapshot.PacketSaysInstancedArea, snapshot.HasCapturedPacket && snapshot.PacketSaysInstancedArea, "Whether the last ZoneInit packet marked the area as instanced"),
                SummaryRow("ReplayCache.Status", replayManager == null ? "<unavailable>" : FormatEnumValue(replayManager->Status), replayManager == null ? 0 : (byte)replayManager->Status, "ContentsReplayManager status flags"),
                SummaryRow("ReplayCache.TerritoryTypeId", hasReplayZoneInit ? ResolveTerritoryName(replayManager->ZoneInitPacket.TerritoryTypeId) : "<unavailable>", hasReplayZoneInit ? replayManager->ZoneInitPacket.TerritoryTypeId : 0, "Territory id from the ZoneInitPacket cached on ContentsReplayManager"),
                SummaryRow("ReplayCache.ServerId", hasReplayZoneInit ? replayManager->ZoneInitPacket.ServerId : "<unavailable>", hasReplayZoneInit ? replayManager->ZoneInitPacket.ServerId : 0, "Server id from the ZoneInitPacket cached on ContentsReplayManager"),
                SummaryRow("ReplayCache.Instance", hasReplayZoneInit ? replayManager->ZoneInitPacket.Instance : "<unavailable>", hasReplayZoneInit ? replayManager->ZoneInitPacket.Instance : 0, "ZoneInitPacket.Instance cached on ContentsReplayManager"),
                SummaryRow("ReplayCache.ContentFinderConditionId", hasReplayZoneInit ? ResolveContentFinderConditionName(replayManager->ZoneInitPacket.ContentFinderConditionId) : "<unavailable>", hasReplayZoneInit ? replayManager->ZoneInitPacket.ContentFinderConditionId : 0, "ContentFinderCondition id from the cached ZoneInitPacket"),
                SummaryRow("ReplayCache.TransitionTerritoryFilterKey", hasReplayZoneInit ? replayManager->ZoneInitPacket.TransitionTerritoryFilterKey : "<unavailable>", hasReplayZoneInit ? replayManager->ZoneInitPacket.TransitionTerritoryFilterKey : 0, "Transition territory filter key from the cached ZoneInitPacket"),
                SummaryRow("ReplayCache.PopRangeId", hasReplayZoneInit ? replayManager->ZoneInitPacket.PopRangeId : "<unavailable>", hasReplayZoneInit ? replayManager->ZoneInitPacket.PopRangeId : 0, "PopRangeId from the cached ZoneInitPacket"),
                SummaryRow("ReplayCache.Flags", hasReplayZoneInit ? FormatZoneInitFlags(replayManager->ZoneInitPacket.Flags) : "<unavailable>", hasReplayZoneInit ? (ushort)replayManager->ZoneInitPacket.Flags : 0, "Zone-init flags stored on ContentsReplayManager"),
                SummaryRow("GameMain.CurrentTerritoryFilterKey", gameMain == null ? 0 : gameMain->CurrentTerritoryFilterKey, gameMain == null ? 0 : gameMain->CurrentTerritoryFilterKey, "Current territory filter key from GameMain"),
                SummaryRow("GameMain.TransitionTerritoryFilterKey", gameMain == null ? 0 : gameMain->TransitionTerritoryFilterKey, gameMain == null ? 0 : gameMain->TransitionTerritoryFilterKey, "Transition territory filter key from GameMain"),
                SummaryRow("AgentMap.CurrentMapMarkerRange", agentMap == null ? 0 : agentMap->CurrentMapMarkerRange, agentMap == null ? 0 : agentMap->CurrentMapMarkerRange, "Current map marker range from AgentMap"),
                SummaryRow("AgentMap.SelectedMapMarkerRange", agentMap == null ? 0 : agentMap->SelectedMapMarkerRange, agentMap == null ? 0 : agentMap->SelectedMapMarkerRange, "Selected map marker range from AgentMap"),
                SummaryRow("PublicContent.LGBPopRange", publicContentDirector == null ? 0 : publicContentDirector->LGBPopRange, publicContentDirector == null ? 0 : publicContentDirector->LGBPopRange, "Active public-content pop-range field"),
                SummaryRow("PublicContent.LGBEventRange", publicContentDirector == null ? 0 : publicContentDirector->LGBEventRange, publicContentDirector == null ? 0 : publicContentDirector->LGBEventRange, "Active public-content event-range field")
            });
    }

    private ClientStructsSheetSnapshot ReadContentsFinder(ClientStructsSheetDefinition definition)
    {
        var contentsFinder = ContentsFinder.Instance();
        if (contentsFinder == null)
            return CreateEmptySnapshot(definition, "ContentsFinder.Instance() returned null.");

        var instanceContent = UIState.Instance();
        var queueInfo = contentsFinder->GetQueueInfo();
        var queuedEntries = string.Empty;
        if (queueInfo != null)
        {
            var parts = new List<string>();
            var entries = queueInfo->QueuedEntries;
            for (var i = 0; i < entries.Length; i++)
            {
                var entry = entries[i];
                if (entry.ContentType == AgentContentsType.None && entry.Id == 0)
                    continue;

                parts.Add(ResolveQueueEntry(entry));
            }

            queuedEntries = string.Join(" | ", parts);
        }

        return CreateSummarySnapshot(
            definition,
            $"Pointer: {FormatPointer(contentsFinder)}",
            "ContentsFinder and queue snapshot.",
            new[]
            {
                SummaryRow("Pointer", FormatPointer(contentsFinder), ((nint)contentsFinder).ToString("X", CultureInfo.InvariantCulture), "ContentsFinder singleton address"),
                SummaryRow("LootRules", FormatEnumValue(contentsFinder->LootRules), (byte)contentsFinder->LootRules, "Current loot-rule mode"),
                SummaryRow("IsUnrestrictedParty", contentsFinder->IsUnrestrictedParty, contentsFinder->IsUnrestrictedParty, "Unrestricted party toggle"),
                SummaryRow("IsMinimalIL", contentsFinder->IsMinimalIL, contentsFinder->IsMinimalIL, "Minimum item level toggle"),
                SummaryRow("IsSilenceEcho", contentsFinder->IsSilenceEcho, contentsFinder->IsSilenceEcho, "Silence Echo toggle"),
                SummaryRow("IsExplorerMode", contentsFinder->IsExplorerMode, contentsFinder->IsExplorerMode, "Explorer mode toggle"),
                SummaryRow("IsLevelSync", contentsFinder->IsLevelSync, contentsFinder->IsLevelSync, "Level sync toggle"),
                SummaryRow("IsLimitedLevelingRoulette", contentsFinder->IsLimitedLevelingRoulette, contentsFinder->IsLimitedLevelingRoulette, "Limited leveling roulette toggle"),
                SummaryRow("QueueState", queueInfo == null ? "Unavailable" : FormatEnumValue(queueInfo->QueueState), queueInfo == null ? 0 : (byte)queueInfo->QueueState, "Current duty-finder queue state"),
                SummaryRow("InfoState.ContentType", queueInfo == null ? "Unavailable" : FormatEnumValue(queueInfo->InfoState.ContentType), queueInfo == null ? 0 : (byte)queueInfo->InfoState.ContentType, "Queue info-state content type"),
                SummaryRow("InfoState.IsReservingServer", queueInfo != null && queueInfo->InfoState.IsReservingServer, queueInfo != null && queueInfo->InfoState.IsReservingServer, "Whether the queue is reserving a server"),
                SummaryRow("QueuedEntries", string.IsNullOrWhiteSpace(queuedEntries) ? "<none>" : queuedEntries, queuedEntries, "Queued roulette/duty entries"),
                SummaryRow("QueuedClassJobId", queueInfo == null ? 0 : queueInfo->QueuedClassJobId, queueInfo == null ? 0 : queueInfo->QueuedClassJobId, "Queued class/job row id"),
                SummaryRow("QueuedContentRouletteId", queueInfo == null ? 0 : ResolveContentRouletteName(queueInfo->QueuedContentRouletteId), queueInfo == null ? 0 : queueInfo->QueuedContentRouletteId, "Queued roulette row id when applicable"),
                SummaryRow("PositionInQueue", queueInfo == null ? 0 : queueInfo->PositionInQueue, queueInfo == null ? 0 : queueInfo->PositionInQueue, "Reported queue position"),
                SummaryRow("ClampedPositionInQueue", queueInfo == null ? 0 : queueInfo->ClampedPositionInQueue, queueInfo == null ? 0 : queueInfo->ClampedPositionInQueue, "Clamped queue position"),
                SummaryRow("EnteredQueueTimestamp", queueInfo == null ? "0" : FormatUnixTimestamp(queueInfo->EnteredQueueTimestamp), queueInfo == null ? 0 : queueInfo->EnteredQueueTimestamp, "Queue-entry timestamp"),
                SummaryRow("QueueReadyTimestamp", queueInfo == null ? "0" : FormatUnixTimestamp(queueInfo->QueueReadyTimestamp), queueInfo == null ? 0 : queueInfo->QueueReadyTimestamp, "Queue-ready timestamp"),
                SummaryRow("NextQueueUpdateTimestamp", queueInfo == null ? "0" : FormatUnixTimestamp(queueInfo->NextQueueUpdateTimestamp), queueInfo == null ? 0 : queueInfo->NextQueueUpdateTimestamp, "Next queue-update timestamp"),
                SummaryRow("PoppedQueueEntry", queueInfo == null ? "<none>" : ResolveQueueEntry(queueInfo->PoppedQueueEntry), queueInfo == null ? string.Empty : ResolveQueueEntry(queueInfo->PoppedQueueEntry), "Duty/roulette entry that has currently popped"),
                SummaryRow("PoppedContentFlags", queueInfo == null ? "<none>" : $"Unrestricted={queueInfo->PoppedContentIsUnrestrictedParty} MinimalIL={queueInfo->PoppedContentIsMinimalIL} LevelSync={queueInfo->PoppedContentIsLevelSync} SilenceEcho={queueInfo->PoppedContentIsSilenceEcho} Explorer={queueInfo->PoppedContentIsExplorerMode}", queueInfo == null ? string.Empty : $"{queueInfo->PoppedContentIsUnrestrictedParty},{queueInfo->PoppedContentIsMinimalIL},{queueInfo->PoppedContentIsLevelSync},{queueInfo->PoppedContentIsSilenceEcho},{queueInfo->PoppedContentIsExplorerMode}", "Toggles attached to the currently popped content"),
                SummaryRow("DutyPenaltyMinutes", instanceContent == null ? 0 : instanceContent->InstanceContent.GetPenaltyRemainingInMinutes(0), instanceContent == null ? 0 : instanceContent->InstanceContent.GetPenaltyRemainingInMinutes(0), "Duty Finder penalty timer in minutes"),
                SummaryRow("InactivityPenaltyMinutes", instanceContent == null ? 0 : instanceContent->InstanceContent.GetPenaltyRemainingInMinutes(1), instanceContent == null ? 0 : instanceContent->InstanceContent.GetPenaltyRemainingInMinutes(1), "Inactivity / PvP penalty timer in minutes"),
                SummaryRow("RankedCrystallineConflictHostingDataCenterId", instanceContent == null ? 0 : instanceContent->InstanceContent.RankedCrystallineConflictHostingDataCenterId, instanceContent == null ? 0 : instanceContent->InstanceContent.RankedCrystallineConflictHostingDataCenterId, "Hosting data center id for ranked CC queue state"),
                SummaryRow("IsLimitedTimeBonusActive", instanceContent != null && instanceContent->InstanceContent.IsLimitedTimeBonusActive, instanceContent != null && instanceContent->InstanceContent.IsLimitedTimeBonusActive, "Limited-time bonus flag")
            });
    }

    private ClientStructsSheetSnapshot ReadEventFramework(ClientStructsSheetDefinition definition)
    {
        var eventFramework = EventFramework.Instance();
        if (eventFramework == null)
            return CreateEmptySnapshot(definition, "EventFramework.Instance() returned null.");

        var contentDirector = eventFramework->GetContentDirector();
        var instanceContentDirector = eventFramework->GetInstanceContentDirector();
        var publicContentDirector = eventFramework->GetPublicContentDirector();
        var eurekaDirector = EventFramework.GetPublicContentDirectorByType(PublicContentDirectorType.Eureka);

        return CreateSummarySnapshot(
            definition,
            $"Pointer: {FormatPointer(eventFramework)}",
            "Live EventFramework and director snapshot.",
            new[]
            {
                SummaryRow("Pointer", FormatPointer(eventFramework), ((nint)eventFramework).ToString("X", CultureInfo.InvariantCulture), "EventFramework singleton address"),
                SummaryRow("LoadState", eventFramework->LoadState, eventFramework->LoadState, "0-6 lifecycle state for event framework modules"),
                SummaryRow("DirectorModule.ActiveContentDirector", FormatPointer(eventFramework->DirectorModule.ActiveContentDirector), eventFramework->DirectorModule.ActiveContentDirector == null ? "0" : ((nint)eventFramework->DirectorModule.ActiveContentDirector).ToString("X", CultureInfo.InvariantCulture), "DirectorModule active content-director pointer"),
                SummaryRow("GetContentDirector()", FormatPointer(contentDirector), contentDirector == null ? "0" : ((nint)contentDirector).ToString("X", CultureInfo.InvariantCulture), "Generic active content director"),
                SummaryRow("GetInstanceContentDirector()", FormatPointer(instanceContentDirector), instanceContentDirector == null ? "0" : ((nint)instanceContentDirector).ToString("X", CultureInfo.InvariantCulture), "Active instance-content director pointer"),
                SummaryRow("GetPublicContentDirector()", FormatPointer(publicContentDirector), publicContentDirector == null ? "0" : ((nint)publicContentDirector).ToString("X", CultureInfo.InvariantCulture), "Active public-content director pointer"),
                SummaryRow("GetPublicContentDirectorByType(Eureka)", FormatPointer(eurekaDirector), eurekaDirector == null ? "0" : ((nint)eurekaDirector).ToString("X", CultureInfo.InvariantCulture), "Direct lookup for the Eureka public-content director"),
                SummaryRow("CurrentContentType", FormatContentType(EventFramework.GetCurrentContentType()), (byte)EventFramework.GetCurrentContentType(), "Current EventFramework content-type enum"),
                SummaryRow("CurrentContentId", EventFramework.GetCurrentContentId(), EventFramework.GetCurrentContentId(), "Current EventFramework content id"),
                SummaryRow("CanLeaveCurrentContent()", EventFramework.CanLeaveCurrentContent(), EventFramework.CanLeaveCurrentContent(), "Static helper for leaving the active content"),
                SummaryRow("ActiveDirector.ContentId", contentDirector == null ? 0 : contentDirector->ContentId, contentDirector == null ? 0 : contentDirector->ContentId, "Active director content id"),
                SummaryRow("ActiveDirector.Sequence", contentDirector == null ? 0 : contentDirector->Sequence, contentDirector == null ? 0 : contentDirector->Sequence, "Active director sequence byte"),
                SummaryRow("ActiveDirector.EventItemId", contentDirector == null ? 0 : contentDirector->EventItemId, contentDirector == null ? 0 : contentDirector->EventItemId, "Active director event-item id"),
                SummaryRow("PublicDirector.Type", publicContentDirector == null ? "None" : FormatPublicContentDirectorType(publicContentDirector->Type), publicContentDirector == null ? 0 : (byte)publicContentDirector->Type, "Current public-content director type when available"),
                SummaryRow("PublicDirector.ContentFinderCondition", publicContentDirector == null ? "0" : ResolveContentFinderConditionName(publicContentDirector->ContentFinderCondition), publicContentDirector == null ? 0 : publicContentDirector->ContentFinderCondition, "Content finder condition attached to the active public-content director"),
                SummaryRow("PublicDirector.AdditionalData", publicContentDirector == null ? 0 : publicContentDirector->AdditionalData, publicContentDirector == null ? 0 : publicContentDirector->AdditionalData, "Public-content additional data field")
            });
    }

    private ClientStructsSheetSnapshot ReadContentDirector(ClientStructsSheetDefinition definition)
    {
        var eventFramework = EventFramework.Instance();
        if (eventFramework == null)
            return CreateEmptySnapshot(definition, "EventFramework.Instance() returned null.");

        var contentDirector = eventFramework->GetContentDirector();
        if (contentDirector == null)
            return CreateEmptySnapshot(definition, "EventFramework has no active content director.");

        var instanceContentDirector = eventFramework->GetInstanceContentDirector();
        var publicContentDirector = eventFramework->GetPublicContentDirector();
        var directorKind = instanceContentDirector != null
            ? "InstanceContentDirector"
            : publicContentDirector != null
                ? "PublicContentDirector"
                : "ContentDirector";

        uint currentLevel = 0;
        uint maxLevel = 0;
        uint contentTimeMaxSeconds = 0;
        try { currentLevel = contentDirector->GetCurrentLevel(); } catch { }
        try { maxLevel = contentDirector->GetMaxLevel(); } catch { }
        try { contentTimeMaxSeconds = contentDirector->GetContentTimeMax(); } catch { }

        var mapEffects = contentDirector->MapEffects;

        return CreateSummarySnapshot(
            definition,
            $"Pointer: {FormatPointer(contentDirector)}",
            $"Active director snapshot ({directorKind}).",
            new[]
            {
                SummaryRow("Pointer", FormatPointer(contentDirector), ((nint)contentDirector).ToString("X", CultureInfo.InvariantCulture), "Active content-director pointer"),
                SummaryRow("DirectorKind", directorKind, directorKind, "Most specific resolved active director type"),
                SummaryRow("ContentId", contentDirector->ContentId, contentDirector->ContentId, "Active director content id"),
                SummaryRow("ContentFlags", contentDirector->ContentFlags, contentDirector->ContentFlags, "Raw content flags byte"),
                SummaryRow("Sequence", contentDirector->Sequence, contentDirector->Sequence, "Current director sequence"),
                SummaryRow("Title", contentDirector->Title.ToString(), contentDirector->Title.ToString(), "Director title text"),
                SummaryRow("Objective", contentDirector->Objective.ToString(), contentDirector->Objective.ToString(), "Director objective text"),
                SummaryRow("ReliefText", contentDirector->ReliefText.ToString(), contentDirector->ReliefText.ToString(), "Director relief text"),
                SummaryRow("EventItemId", contentDirector->EventItemId, contentDirector->EventItemId, "Active event-item id"),
                SummaryRow("DirectorStartTimestamp", FormatUnixTimestamp(contentDirector->DirectorStartTimestamp), contentDirector->DirectorStartTimestamp, "Director start timestamp"),
                SummaryRow("DirectorEndTimestamp", FormatUnixTimestamp(contentDirector->DirectorEndTimestamp), contentDirector->DirectorEndTimestamp, "Director end timestamp"),
                SummaryRow("ContentTimeLeft", contentDirector->ContentTimeLeft.ToString("F1", CultureInfo.InvariantCulture), contentDirector->ContentTimeLeft, "Remaining content time reported by ContentDirector"),
                SummaryRow("GetContentTimeMax()", contentTimeMaxSeconds, contentTimeMaxSeconds, "Maximum content time in seconds"),
                SummaryRow("GetCurrentLevel()", currentLevel, currentLevel, "Virtual current-level helper"),
                SummaryRow("GetMaxLevel()", maxLevel, maxLevel, "Virtual max-level helper"),
                SummaryRow("MapEffects", FormatPointer(mapEffects), mapEffects == null ? "0" : ((nint)mapEffects).ToString("X", CultureInfo.InvariantCulture), "Pointer to the live ContentDirector map-effect list"),
                SummaryRow("MapEffects.ItemCount", mapEffects == null ? 0 : mapEffects->ItemCount, mapEffects == null ? 0 : mapEffects->ItemCount, "Number of live shared-group/map-effect items"),
                SummaryRow("MapEffects.ContentDirectorManagedSGRowId", mapEffects == null ? 0 : mapEffects->ContentDirectorManagedSGRowId, mapEffects == null ? 0 : mapEffects->ContentDirectorManagedSGRowId, "Backing managed shared-group row id"),
                SummaryRow("MapEffects.Dirty", mapEffects == null ? 0 : mapEffects->Dirty, mapEffects == null ? 0 : mapEffects->Dirty, "Dirty flag for live map effects"),
                SummaryRow("DirectorTodos.Count", contentDirector->DirectorTodos.Count, contentDirector->DirectorTodos.Count, "Number of active director todo entries"),
                SummaryRow("InstanceContentType", instanceContentDirector == null ? "Unavailable" : FormatEnumValue(instanceContentDirector->InstanceContentType), instanceContentDirector == null ? 0 : (byte)instanceContentDirector->InstanceContentType, "Typed instance-content category when a duty director is active"),
                SummaryRow("InstanceContent.ReqInstance", instanceContentDirector == null ? 0 : instanceContentDirector->ReqInstance, instanceContentDirector == null ? 0 : instanceContentDirector->ReqInstance, "Instance-content requirement / request field"),
                SummaryRow("InstanceContent.LGBEventRange", instanceContentDirector == null ? 0 : instanceContentDirector->LGBEventRange, instanceContentDirector == null ? 0 : instanceContentDirector->LGBEventRange, "Instance-content LGB event range field"),
                SummaryRow("InstanceContent.InstanceClearGil", instanceContentDirector == null ? 0 : instanceContentDirector->InstanceClearGil, instanceContentDirector == null ? 0 : instanceContentDirector->InstanceClearGil, "Instance clear gil reward"),
                SummaryRow("InstanceContent.InstanceClearExp", instanceContentDirector == null ? 0 : instanceContentDirector->InstanceClearExp, instanceContentDirector == null ? 0 : instanceContentDirector->InstanceClearExp, "Instance clear exp reward"),
                SummaryRow("InstanceContent.RewardItem", instanceContentDirector == null ? "0" : ResolveItemName(instanceContentDirector->InstanceContentRewardItem), instanceContentDirector == null ? 0 : instanceContentDirector->InstanceContentRewardItem, "Instance-content reward item row id"),
                SummaryRow("PublicContent.Type", publicContentDirector == null ? "Unavailable" : FormatPublicContentDirectorType(publicContentDirector->Type), publicContentDirector == null ? 0 : (byte)publicContentDirector->Type, "Public-content type when a public-content director is active"),
                SummaryRow("PublicContent.ContentFinderCondition", publicContentDirector == null ? "0" : ResolveContentFinderConditionName(publicContentDirector->ContentFinderCondition), publicContentDirector == null ? 0 : publicContentDirector->ContentFinderCondition, "Public-content ContentFinderCondition"),
                SummaryRow("PublicContent.Timelimit", publicContentDirector == null ? 0 : publicContentDirector->Timelimit, publicContentDirector == null ? 0 : publicContentDirector->Timelimit, "Public-content timelimit field"),
                SummaryRow("PublicContent.AdditionalData", publicContentDirector == null ? 0 : publicContentDirector->AdditionalData, publicContentDirector == null ? 0 : publicContentDirector->AdditionalData, "Public-content additional data"),
                SummaryRow("PublicContent.MapIcon", publicContentDirector == null ? 0 : publicContentDirector->MapIcon, publicContentDirector == null ? 0 : publicContentDirector->MapIcon, "Public-content map icon id"),
                SummaryRow("PublicContent.LGBEventRange", publicContentDirector == null ? 0 : publicContentDirector->LGBEventRange, publicContentDirector == null ? 0 : publicContentDirector->LGBEventRange, "Public-content LGB event range"),
                SummaryRow("PublicContent.LGBPopRange", publicContentDirector == null ? 0 : publicContentDirector->LGBPopRange, publicContentDirector == null ? 0 : publicContentDirector->LGBPopRange, "Public-content pop-range field that may help with instance/layout correlation")
            });
    }

    private ClientStructsSheetSnapshot ReadDirectorTodos(ClientStructsSheetDefinition definition, ClientStructsSheetRequest request)
    {
        var eventFramework = EventFramework.Instance();
        if (eventFramework == null)
            return CreateEmptySnapshot(definition, "EventFramework.Instance() returned null.");

        var contentDirector = eventFramework->GetContentDirector();
        if (contentDirector == null)
            return CreateEmptySnapshot(definition, "EventFramework has no active content director.");

        var todos = contentDirector->DirectorTodos;
        var totalRows = todos.Count;
        var columns = CreateColumns(
            Column("Idx", "Index", "Director todo vector index", 56f),
            Column("Enabled", "bool", "Whether the todo row is enabled", 66f),
            Column("Complete", "bool", "Whether the todo row is complete", 72f),
            Column("Check", "bool", "Whether the row should gray out on completion", 66f),
            Column("Type", "enum", "Todo type discriminator", 90f),
            Column("Text", "Utf8String", "Todo text payload", 320f),
            Column("Current", "int", "Current progress/count value", 74f),
            Column("Needed", "int", "Needed progress/count value", 74f),
            Column("EndTime", "long", "Todo end timestamp when used", 150f),
            Column("Duration", "long", "Todo duration in seconds when used", 110f));

        var visibleRows = new List<ClientStructsSheetRow>();
        BuildWindow(totalRows, request.StartIndex, request.RowCount, out var startIndex, out var rowsToLoad);
        for (var offset = 0; offset < rowsToLoad; offset++)
        {
            var rowIndex = startIndex + offset;
            var todo = todos[rowIndex];
            visibleRows.Add(CreateRow(
                rowIndex,
                (uint)rowIndex,
                Cell(rowIndex),
                Cell(todo.Enabled),
                Cell(todo.Complete),
                Cell(todo.CheckOnCompletion),
                Cell(FormatEnumValue(todo.Type), (int)todo.Type),
                Cell(todo.Text.ToString(), todo.Text.ToString()),
                Cell(todo.CurrentCount),
                Cell(todo.NeededCount),
                Cell(FormatUnixTimestamp((int)todo.EndTimestamp), todo.EndTimestamp),
                Cell(todo.Duration)));
        }

        var message = $"Rows {startIndex + 1}-{startIndex + visibleRows.Count} of {totalRows} - Active director todos";
        return CreateSnapshot(definition, columns, visibleRows, totalRows, startIndex, request.RowCount, $"Pointer: {FormatPointer(contentDirector)}", message);
    }

    private ClientStructsSheetSnapshot ReadDynamicEvents(ClientStructsSheetDefinition definition, ClientStructsSheetRequest request)
    {
        var eventFramework = EventFramework.Instance();
        var publicDirector = eventFramework == null ? null : eventFramework->GetPublicContentDirector();

        DynamicEventContainer* container = null;
        var source = "DynamicEventContainer.GetInstance()";

        if (publicDirector != null)
        {
            switch (publicDirector->Type)
            {
                case PublicContentDirectorType.Bozja:
                {
                    var bozja = PublicContentBozja.GetInstance();
                    if (bozja != null)
                    {
                        container = &bozja->DynamicEventContainer;
                        source = "PublicContentBozja.DynamicEventContainer";
                    }
                    break;
                }
                case PublicContentDirectorType.OccultCrescent:
                {
                    var occult = PublicContentOccultCrescent.GetInstance();
                    if (occult != null)
                    {
                        container = &occult->DynamicEventContainer;
                        source = "PublicContentOccultCrescent.DynamicEventContainer";
                    }
                    break;
                }
            }
        }

        if (container == null)
            container = DynamicEventContainer.GetInstance();
        if (container == null)
            return CreateEmptySnapshot(definition, "No dynamic-event container is available.");

        var rows = new List<(int Slot, InstanceDynamicEvent Event)>();
        for (var i = 0; i < container->Events.Length; i++)
        {
            var dynamicEvent = container->Events[i];
            var name = dynamicEvent.Name.ToString();
            if (dynamicEvent.DynamicEventId == 0
                && string.IsNullOrWhiteSpace(name)
                && dynamicEvent.State == DynamicEventState.Inactive
                && dynamicEvent.MapMarker.MapId == 0)
                continue;

            rows.Add((Slot: i, Event: dynamicEvent));
        }

        var columns = CreateColumns(
            Column("Slot", "Index", "Dynamic-event array slot", 56f),
            Column("DynamicEventId", "ushort", "DynamicEventId field", 96f),
            Column("State", "enum", "Dynamic-event runtime state", 110f),
            Column("Name", "Utf8String", "Runtime event name", 220f),
            Column("Description", "Utf8String", "Runtime event description", 260f),
            Column("Progress", "byte", "Progress percentage or stage", 70f),
            Column("Participants", "byte", "Current participants", 82f),
            Column("MaxParticipants", "byte", "Maximum participants", 96f),
            Column("SecondsLeft", "uint", "Seconds left", 96f),
            Column("Quest", "Quest", "Quest row id attached to the event", 180f),
            Column("MapId", "uint", "Attached map id from MapMarkerData", 84f),
            Column("Territory", "ushort", "Attached territory from MapMarkerData", 180f),
            Column("Position", "Vector3", "Attached world position from MapMarkerData", 180f),
            Column("Radius", "float", "Attached radius from MapMarkerData", 76f));

        var visibleRows = new List<ClientStructsSheetRow>();
        BuildWindow(rows.Count, request.StartIndex, request.RowCount, out var startIndex, out var rowsToLoad);
        for (var offset = 0; offset < rowsToLoad; offset++)
        {
            var rowIndex = startIndex + offset;
            var entry = rows[rowIndex];
            var dynamicEvent = entry.Event;
            visibleRows.Add(CreateRow(
                rowIndex,
                dynamicEvent.DynamicEventId,
                Cell(entry.Slot),
                Cell(dynamicEvent.DynamicEventId),
                Cell(FormatEnumValue(dynamicEvent.State), (byte)dynamicEvent.State),
                Cell(dynamicEvent.Name.ToString(), dynamicEvent.Name.ToString()),
                Cell(dynamicEvent.Description.ToString(), dynamicEvent.Description.ToString()),
                Cell(dynamicEvent.Progress),
                Cell(dynamicEvent.Participants),
                Cell(dynamicEvent.MaxParticipants),
                Cell(dynamicEvent.SecondsLeft),
                Cell(ResolveQuestRowName(dynamicEvent.Quest), dynamicEvent.Quest),
                Cell(dynamicEvent.MapMarker.MapId),
                Cell(ResolveTerritoryName(dynamicEvent.MapMarker.TerritoryTypeId), dynamicEvent.MapMarker.TerritoryTypeId),
                Cell(FormatVector3(dynamicEvent.MapMarker.Position), FormatVector3(dynamicEvent.MapMarker.Position)),
                Cell(dynamicEvent.MapMarker.Radius.ToString("F1", CultureInfo.InvariantCulture), dynamicEvent.MapMarker.Radius)));
        }

        var message = $"Rows {startIndex + 1}-{startIndex + visibleRows.Count} of {rows.Count} - CurrentEventId: {container->CurrentEventId} - CurrentEventIndex: {container->CurrentEventIndex} - Source: {source}";
        return CreateSnapshot(definition, columns, visibleRows, rows.Count, startIndex, request.RowCount, $"Pointer: {FormatPointer(container)}", message);
    }

    private ClientStructsSheetSnapshot ReadEurekaStatus(ClientStructsSheetDefinition definition)
    {
        var gameMain = GameMain.Instance();
        var playerState = PlayerState.Instance();
        var uiState = UIState.Instance();
        var eurekaDirector = (PublicContentEureka*)EventFramework.GetPublicContentDirectorByType(PublicContentDirectorType.Eureka);
        var eurekaPublicDirector = eurekaDirector == null ? null : (PublicContentDirector*)eurekaDirector;
        var framework = FrameworkSystem.Instance();
        var proxy = framework == null ? null : framework->NetworkModuleProxy;
        var networkModule = proxy == null ? null : proxy->NetworkModule;
        var proxyCurrentInstance = proxy == null ? (short)0 : proxy->GetCurrentInstance();
        var zoneInit = zoneInstanceSnapshotService.GetSnapshot();
        var replayManager = ContentsReplayManager.Instance();
        var hasReplayZoneInit = replayManager != null && replayManager->ZoneInitPacket.TerritoryTypeId != 0;
        var agentMap = TryGetAgentMap();
        var preferredInstanceCandidate = ResolvePreferredInstanceCandidate(
            uiState == null ? 0u : uiState->PublicInstance.InstanceId,
            proxyCurrentInstance,
            networkModule == null ? (short)0 : networkModule->CurrentInstance,
            Plugin.ClientState.Instance,
            zoneInit,
            replayManager,
            hasReplayZoneInit,
            out var preferredInstanceSource);
        var preferredServerIdCandidate = ResolvePreferredServerIdCandidate(zoneInit, replayManager, hasReplayZoneInit, out var preferredServerIdSource);
        var preferredPopRangeCandidate = ResolvePreferredPopRangeCandidate(zoneInit, replayManager, hasReplayZoneInit, eurekaPublicDirector, out var preferredPopRangeSource);

        return CreateSummarySnapshot(
            definition,
            $"EurekaDirector: {FormatPointer(eurekaDirector)}",
            "Aggregated Eureka runtime state pulled from multiple ClientStructs singletons.",
            new[]
            {
                SummaryRow("InEurekaTerritory", gameMain != null && gameMain->CurrentTerritoryIntendedUseId == ClientTerritoryIntendedUse.Eureka, gameMain != null && gameMain->CurrentTerritoryIntendedUseId == ClientTerritoryIntendedUse.Eureka, "True when GameMain reports TerritoryIntendedUse.Eureka"),
                SummaryRow("CurrentTerritory", gameMain == null ? "0" : ResolveTerritoryName(gameMain->CurrentTerritoryTypeId), gameMain == null ? 0 : gameMain->CurrentTerritoryTypeId, "Current territory resolved through Lumina"),
                SummaryRow("CurrentContentFinderCondition", gameMain == null ? "0" : ResolveContentFinderConditionName(gameMain->CurrentContentFinderConditionId), gameMain == null ? 0 : gameMain->CurrentContentFinderConditionId, "Current content finder condition resolved through Lumina"),
                SummaryRow("PreferredInstanceCandidate", preferredInstanceCandidate == 0 ? "<none>" : preferredInstanceCandidate, preferredInstanceCandidate, "First non-zero candidate from UIState.PublicInstance.InstanceId -> NetworkModuleProxy.GetCurrentInstance() -> NetworkModule.CurrentInstance -> Dalamud.ClientState.Instance -> LastZoneInit.PacketInstance -> ReplayZoneInit.Instance"),
                SummaryRow("PreferredInstanceSource", preferredInstanceSource, preferredInstanceSource, "Source currently supplying PreferredInstanceCandidate"),
                SummaryRow("PreferredServerIdCandidate", preferredServerIdCandidate == 0 ? "<none>" : preferredServerIdCandidate, preferredServerIdCandidate, "First non-zero server-id candidate from LastZoneInit.ServerId -> ReplayZoneInit.ServerId"),
                SummaryRow("PreferredServerIdSource", preferredServerIdSource, preferredServerIdSource, "Source currently supplying PreferredServerIdCandidate"),
                SummaryRow("PreferredPopRangeCandidate", preferredPopRangeCandidate == 0 ? "<none>" : preferredPopRangeCandidate, preferredPopRangeCandidate, "First non-zero layout/pop-range candidate from LastZoneInit.PopRangeId -> ReplayZoneInit.PopRangeId -> PublicContentEureka.LGBPopRange"),
                SummaryRow("PreferredPopRangeSource", preferredPopRangeSource, preferredPopRangeSource, "Source currently supplying PreferredPopRangeCandidate"),
                SummaryRow("PublicInstance.InstanceId", uiState == null ? 0 : uiState->PublicInstance.InstanceId, uiState == null ? 0 : uiState->PublicInstance.InstanceId, "UIState public-instance id"),
                SummaryRow("Network.GetCurrentInstance()", proxyCurrentInstance, proxyCurrentInstance, "NetworkModuleProxy current instance"),
                SummaryRow("NetworkModule.CurrentInstance", networkModule == null ? 0 : networkModule->CurrentInstance, networkModule == null ? 0 : networkModule->CurrentInstance, "Backing short field stored in NetworkModule"),
                SummaryRow("Dalamud.ClientState.Instance", Plugin.ClientState.Instance, Plugin.ClientState.Instance, "Dalamud client-state instance maintained from NetworkModuleProxy.SetCurrentInstance"),
                SummaryRow("LastZoneInit.ServerId", zoneInit.HasCapturedPacket ? zoneInit.ServerId : "<unavailable>", zoneInit.HasCapturedPacket ? zoneInit.ServerId : 0, "Server id from the last captured ZoneInit packet; old EurekaHelper-style shard research should compare this against instance and pop-range data"),
                SummaryRow("LastZoneInit.Instance", zoneInit.HasCapturedPacket ? zoneInit.PacketInstance : "<unavailable>", zoneInit.HasCapturedPacket ? zoneInit.PacketInstance : 0, "Packet `Instance` from the last captured ZoneInit packet"),
                SummaryRow("LastZoneInit.TerritoryType", zoneInit.HasCapturedPacket ? ResolveTerritoryName(zoneInit.TerritoryTypeId) : "<unavailable>", zoneInit.HasCapturedPacket ? zoneInit.TerritoryTypeId : 0, "Territory id from the last captured ZoneInit packet"),
                SummaryRow("LastZoneInit.ContentFinderCondition", zoneInit.HasCapturedPacket ? ResolveContentFinderConditionName(zoneInit.ContentFinderConditionId) : "<unavailable>", zoneInit.HasCapturedPacket ? zoneInit.ContentFinderConditionId : 0, "Content finder condition id from the last captured ZoneInit packet"),
                SummaryRow("LastZoneInit.PopRangeId", zoneInit.HasCapturedPacket ? zoneInit.PopRangeId : "<unavailable>", zoneInit.HasCapturedPacket ? zoneInit.PopRangeId : 0, "Packet `PopRangeId` from the last captured ZoneInit packet; ClientStructs notes this as the PlanMap instance id"),
                SummaryRow("LastZoneInit.IsInstancedArea", zoneInit.HasCapturedPacket && zoneInit.PacketSaysInstancedArea, zoneInit.HasCapturedPacket && zoneInit.PacketSaysInstancedArea, "Whether the last captured ZoneInit packet marked the area as instanced"),
                SummaryRow("ReplayZoneInit.ServerId", hasReplayZoneInit ? replayManager->ZoneInitPacket.ServerId : "<unavailable>", hasReplayZoneInit ? replayManager->ZoneInitPacket.ServerId : 0, "Server id from the ZoneInitPacket cached on ContentsReplayManager"),
                SummaryRow("ReplayZoneInit.Instance", hasReplayZoneInit ? replayManager->ZoneInitPacket.Instance : "<unavailable>", hasReplayZoneInit ? replayManager->ZoneInitPacket.Instance : 0, "ZoneInitPacket.Instance cached on ContentsReplayManager"),
                SummaryRow("ReplayZoneInit.TerritoryType", hasReplayZoneInit ? ResolveTerritoryName(replayManager->ZoneInitPacket.TerritoryTypeId) : "<unavailable>", hasReplayZoneInit ? replayManager->ZoneInitPacket.TerritoryTypeId : 0, "Territory id from the ZoneInitPacket cached on ContentsReplayManager"),
                SummaryRow("ReplayZoneInit.ContentFinderCondition", hasReplayZoneInit ? ResolveContentFinderConditionName(replayManager->ZoneInitPacket.ContentFinderConditionId) : "<unavailable>", hasReplayZoneInit ? replayManager->ZoneInitPacket.ContentFinderConditionId : 0, "Content finder condition id from the cached ZoneInitPacket"),
                SummaryRow("ReplayZoneInit.PopRangeId", hasReplayZoneInit ? replayManager->ZoneInitPacket.PopRangeId : "<unavailable>", hasReplayZoneInit ? replayManager->ZoneInitPacket.PopRangeId : 0, "ZoneInitPacket.PopRangeId cached on ContentsReplayManager"),
                SummaryRow("GameMain.CurrentTerritoryFilterKey", gameMain == null ? 0 : gameMain->CurrentTerritoryFilterKey, gameMain == null ? 0 : gameMain->CurrentTerritoryFilterKey, "Current territory filter key from GameMain"),
                SummaryRow("GameMain.TransitionTerritoryFilterKey", gameMain == null ? 0 : gameMain->TransitionTerritoryFilterKey, gameMain == null ? 0 : gameMain->TransitionTerritoryFilterKey, "Transition territory filter key from GameMain"),
                SummaryRow("AgentMap.CurrentMapMarkerRange", agentMap == null ? 0 : agentMap->CurrentMapMarkerRange, agentMap == null ? 0 : agentMap->CurrentMapMarkerRange, "Current map marker range from AgentMap"),
                SummaryRow("AgentMap.SelectedMapMarkerRange", agentMap == null ? 0 : agentMap->SelectedMapMarkerRange, agentMap == null ? 0 : agentMap->SelectedMapMarkerRange, "Selected map marker range from AgentMap"),
                SummaryRow("PlayerState.GetContentValue(2)", playerState == null ? 0 : playerState->GetContentValue(2), playerState == null ? 0 : playerState->GetContentValue(2), "Eureka effective elemental level"),
                SummaryRow("PlayerState.GetContentValue(3)", playerState == null ? 0 : playerState->GetContentValue(3), playerState == null ? 0 : playerState->GetContentValue(3), "Eureka elemental sync flag"),
                SummaryRow("PlayerState.GetContentValue(4)", playerState == null ? 0 : playerState->GetContentValue(4), playerState == null ? 0 : playerState->GetContentValue(4), "Eureka current elemental level"),
                SummaryRow("Inspect.GetContentValue(1)", uiState == null ? 0 : uiState->Inspect.GetContentValue(1), uiState == null ? 0 : uiState->Inspect.GetContentValue(1), "Inspect-side Eureka elemental level for the currently inspected target"),
                SummaryRow("Inspect.GetContentValue(2)", uiState == null ? 0 : uiState->Inspect.GetContentValue(2), uiState == null ? 0 : uiState->Inspect.GetContentValue(2), "Inspect-side Eureka sync flag for the currently inspected target"),
                SummaryRow("Inspect.GetContentValue(3)", uiState == null ? 0 : uiState->Inspect.GetContentValue(3), uiState == null ? 0 : uiState->Inspect.GetContentValue(3), "Inspect-side Eureka remaining-time field for the currently inspected target"),
                SummaryRow("PublicContentEureka.Pointer", FormatPointer(eurekaDirector), eurekaDirector == null ? "0" : ((nint)eurekaDirector).ToString("X", CultureInfo.InvariantCulture), "Eureka public-content director pointer; only valid while Eureka content is active"),
                SummaryRow("PublicContentEureka.ContentFinderCondition", eurekaPublicDirector == null ? "0" : ResolveContentFinderConditionName(eurekaPublicDirector->ContentFinderCondition), eurekaPublicDirector == null ? 0 : eurekaPublicDirector->ContentFinderCondition, "ContentFinderCondition attached to the active Eureka director"),
                SummaryRow("PublicContentEureka.LGBEventRange", eurekaPublicDirector == null ? 0 : eurekaPublicDirector->LGBEventRange, eurekaPublicDirector == null ? 0 : eurekaPublicDirector->LGBEventRange, "Eureka public-content event-range field"),
                SummaryRow("PublicContentEureka.LGBPopRange", eurekaPublicDirector == null ? 0 : eurekaPublicDirector->LGBPopRange, eurekaPublicDirector == null ? 0 : eurekaPublicDirector->LGBPopRange, "Eureka public-content pop-range field"),
                SummaryRow("PublicContentEureka.MaxElementalLevel", eurekaDirector == null ? 0 : eurekaDirector->MaxElementalLevel, eurekaDirector == null ? 0 : eurekaDirector->MaxElementalLevel, "Elemental sync cap announced by the Eureka director"),
                SummaryRow("PublicContentEureka.CurrentExperience", eurekaDirector == null ? 0 : eurekaDirector->CurrentExperience, eurekaDirector == null ? 0 : eurekaDirector->CurrentExperience, "Current elemental experience"),
                SummaryRow("PublicContentEureka.NeededExperience", eurekaDirector == null ? 0 : eurekaDirector->NeededExperience, eurekaDirector == null ? 0 : eurekaDirector->NeededExperience, "Experience needed for the next elemental level"),
                SummaryRow("PublicContentEureka.MagiaAetherCharge", eurekaDirector == null ? 0 : eurekaDirector->MagiaAetherCharge, eurekaDirector == null ? 0 : eurekaDirector->MagiaAetherCharge, "Current magia aether charge"),
                SummaryRow("PublicContentEureka.ElementalWheel", eurekaDirector == null ? "Unavailable" : $"Fire:{eurekaDirector->Fire} Ice:{eurekaDirector->Ice} Wind:{eurekaDirector->Wind} Earth:{eurekaDirector->Earth} Lightning:{eurekaDirector->Lightning} Water:{eurekaDirector->Water}", eurekaDirector == null ? string.Empty : $"{eurekaDirector->Fire},{eurekaDirector->Ice},{eurekaDirector->Wind},{eurekaDirector->Earth},{eurekaDirector->Lightning},{eurekaDirector->Water}", "Current magia board elemental allocation"),
                SummaryRow("PublicContentEureka.Magicite", eurekaDirector == null ? 0 : eurekaDirector->Magicite, eurekaDirector == null ? 0 : eurekaDirector->Magicite, "Unlocked magicite count"),
                SummaryRow("PublicContentEureka.MagiaAether", eurekaDirector == null ? 0 : eurekaDirector->MagiaAether, eurekaDirector == null ? 0 : eurekaDirector->MagiaAether, "Magia aether value")
            });
    }

    private ClientStructsSheetSnapshot ReadBozjaStatus(ClientStructsSheetDefinition definition)
    {
        var bozja = PublicContentBozja.GetInstance();
        if (bozja == null)
            return CreateEmptySnapshot(definition, "PublicContentBozja is not active.");

        var container = &bozja->DynamicEventContainer;
        InstanceDynamicEvent* currentEvent = null;
        try { currentEvent = container->GetCurrentEvent(); } catch { }

        var holsterCount = 0;
        foreach (var actionId in bozja->State.HolsterActions)
        {
            if (actionId != 0)
                holsterCount++;
        }

        return CreateSummarySnapshot(
            definition,
            $"Pointer: {FormatPointer(bozja)}",
            "PublicContentBozja snapshot.",
            new[]
            {
                SummaryRow("Pointer", FormatPointer(bozja), ((nint)bozja).ToString("X", CultureInfo.InvariantCulture), "PublicContentBozja pointer"),
                SummaryRow("StateInitialized", bozja->StateInitialized, bozja->StateInitialized, "Whether the Bozja state block is initialized"),
                SummaryRow("CurrentExperience", bozja->State.CurrentExperience, bozja->State.CurrentExperience, "Current Bozja mettle"),
                SummaryRow("NeededExperience", bozja->State.NeededExperience, bozja->State.NeededExperience, "Mettle needed for the next rank"),
                SummaryRow("HolsterActionsLoaded", holsterCount, holsterCount, "Number of non-zero holster action ids"),
                SummaryRow("DynamicEvent.CurrentEventId", container->CurrentEventId, container->CurrentEventId, "Current Bozja dynamic-event id"),
                SummaryRow("DynamicEvent.CurrentEventIndex", container->CurrentEventIndex, container->CurrentEventIndex, "Current dynamic-event slot index"),
                SummaryRow("CurrentDynamicEvent", currentEvent == null ? "<none>" : currentEvent->Name.ToString(), currentEvent == null ? string.Empty : currentEvent->Name.ToString(), "Current dynamic-event runtime name"),
                SummaryRow("CurrentDynamicEventState", currentEvent == null ? "<none>" : FormatEnumValue(currentEvent->State), currentEvent == null ? 0 : (byte)currentEvent->State, "Current dynamic-event runtime state"),
                SummaryRow("CurrentDynamicEventSecondsLeft", currentEvent == null ? 0 : currentEvent->SecondsLeft, currentEvent == null ? 0 : currentEvent->SecondsLeft, "Seconds left on the current Bozja dynamic event")
            });
    }

    private ClientStructsSheetSnapshot ReadQuestSummary(ClientStructsSheetDefinition definition)
    {
        var questManager = QuestManager.Instance();
        if (questManager == null)
            return CreateEmptySnapshot(definition, "QuestManager.Instance() returned null.");

        return CreateSummarySnapshot(
            definition,
            $"Pointer: {FormatPointer(questManager)}",
            "QuestManager summary snapshot.",
            new[]
            {
                SummaryRow("Pointer", FormatPointer(questManager), ((nint)questManager).ToString("X", CultureInfo.InvariantCulture), "Singleton address"),
                SummaryRow("NumAcceptedQuests", questManager->NumAcceptedQuests, questManager->NumAcceptedQuests, "Accepted normal quests"),
                SummaryRow("NumAcceptedDailyQuests", questManager->NumAcceptedDailyQuests, questManager->NumAcceptedDailyQuests, "Accepted daily quests"),
                SummaryRow("NumAcceptedLeveQuests", questManager->NumAcceptedLeveQuests, questManager->NumAcceptedLeveQuests, "Accepted levequests"),
                SummaryRow("NumLeveAllowances", questManager->NumLeveAllowances, questManager->NumLeveAllowances, "Current leve allowances"),
                SummaryRow("GetBeastTribeAllowance()", questManager->GetBeastTribeAllowance(), questManager->GetBeastTribeAllowance(), "Current beast tribe allowance"),
                SummaryRow("NextLeveAllowance", FormatUnixTimestamp(QuestManager.GetNextLeveAllowancesUnixTimestamp()), QuestManager.GetNextLeveAllowancesUnixTimestamp(), "Next leve allowance timestamp")
            });
    }

    private ClientStructsSheetSnapshot ReadActiveQuests(ClientStructsSheetDefinition definition, ClientStructsSheetRequest request)
    {
        var questManager = QuestManager.Instance();
        if (questManager == null)
            return CreateEmptySnapshot(definition, "QuestManager.Instance() returned null.");

        var quests = questManager->NormalQuests;
        var columns = CreateColumns(
            Column("Slot", "Index", "QuestWork array slot", 58f),
            Column("QuestId", "ushort", "Compact quest id stored in QuestManager", 90f),
            Column("Quest", "Lumina Quest", "Lumina-enriched quest name when found", 240f),
            Column("Sequence", "byte", "Live quest progression sequence", 76f),
            Column("AcceptClassJob", "byte", "Accepting class/job id", 110f),
            Column("Flags", "byte", "QuestWork flags byte", 72f));

        var visibleRows = new List<ClientStructsSheetRow>();
        BuildWindow(quests.Length, request.StartIndex, request.RowCount, out var startIndex, out var rowsToLoad);
        for (var offset = 0; offset < rowsToLoad; offset++)
        {
            var rowIndex = startIndex + offset;
            var entry = quests[rowIndex];
            visibleRows.Add(CreateRow(
                rowIndex,
                (uint)rowIndex,
                Cell(rowIndex),
                Cell(entry.QuestId),
                Cell(ResolveQuestName(entry.QuestId), entry.QuestId),
                Cell(entry.Sequence),
                Cell(ResolveClassJobName(entry.AcceptClassJob), entry.AcceptClassJob),
                Cell(entry.Flags)));
        }

        return CreateSnapshot(definition, columns, visibleRows, quests.Length, startIndex, request.RowCount, $"Pointer: {FormatPointer(questManager)}", $"Rows {startIndex + 1}-{startIndex + visibleRows.Count} of {quests.Length} • QuestManager.NormalQuests");
    }

    private ClientStructsSheetSnapshot ReadDailyQuests(ClientStructsSheetDefinition definition, ClientStructsSheetRequest request)
    {
        var questManager = QuestManager.Instance();
        if (questManager == null)
            return CreateEmptySnapshot(definition, "QuestManager.Instance() returned null.");

        var quests = questManager->DailyQuests;
        var columns = CreateColumns(
            Column("Slot", "Index", "DailyQuestWork array slot", 58f),
            Column("QuestId", "ushort", "Compact quest id stored in DailyQuestWork", 90f),
            Column("Quest", "Lumina Quest", "Lumina-enriched quest name when found", 240f),
            Column("IsCompleted", "bool", "Daily quest completion bit", 90f),
            Column("Flags", "byte", "Daily quest flags byte", 72f));

        var visibleRows = new List<ClientStructsSheetRow>();
        BuildWindow(quests.Length, request.StartIndex, request.RowCount, out var startIndex, out var rowsToLoad);
        for (var offset = 0; offset < rowsToLoad; offset++)
        {
            var rowIndex = startIndex + offset;
            var entry = quests[rowIndex];
            visibleRows.Add(CreateRow(
                rowIndex,
                (uint)rowIndex,
                Cell(rowIndex),
                Cell(entry.QuestId),
                Cell(ResolveQuestName(entry.QuestId), entry.QuestId),
                Cell(entry.IsCompleted),
                Cell(entry.Flags)));
        }

        return CreateSnapshot(definition, columns, visibleRows, quests.Length, startIndex, request.RowCount, $"Pointer: {FormatPointer(questManager)}", $"Rows {startIndex + 1}-{startIndex + visibleRows.Count} of {quests.Length} • QuestManager.DailyQuests");
    }

    private ClientStructsSheetSnapshot ReadLeves(ClientStructsSheetDefinition definition, ClientStructsSheetRequest request)
    {
        var questManager = QuestManager.Instance();
        if (questManager == null)
            return CreateEmptySnapshot(definition, "QuestManager.Instance() returned null.");

        var leves = questManager->LeveQuests;
        var columns = CreateColumns(
            Column("Slot", "Index", "LeveWork array slot", 58f),
            Column("LeveId", "ushort", "Leve row id", 90f),
            Column("Sequence", "byte", "Live leve progression sequence", 76f),
            Column("ClearClass", "byte", "Class/job that clears the leve", 110f),
            Column("Flags", "ushort", "Leve flags bitfield", 90f),
            Column("LeveSeed", "ushort", "Leve seed value", 90f));

        var visibleRows = new List<ClientStructsSheetRow>();
        BuildWindow(leves.Length, request.StartIndex, request.RowCount, out var startIndex, out var rowsToLoad);
        for (var offset = 0; offset < rowsToLoad; offset++)
        {
            var rowIndex = startIndex + offset;
            var entry = leves[rowIndex];
            visibleRows.Add(CreateRow(
                rowIndex,
                (uint)rowIndex,
                Cell(rowIndex),
                Cell(entry.LeveId),
                Cell(entry.Sequence),
                Cell(ResolveClassJobName(entry.ClearClass), entry.ClearClass),
                Cell(entry.Flags),
                Cell(entry.LeveSeed)));
        }

        return CreateSnapshot(definition, columns, visibleRows, leves.Length, startIndex, request.RowCount, $"Pointer: {FormatPointer(questManager)}", $"Rows {startIndex + 1}-{startIndex + visibleRows.Count} of {leves.Length} • QuestManager.LeveQuests");
    }

    private ClientStructsSheetSnapshot ReadInventorySummary(ClientStructsSheetDefinition definition)
    {
        var inventoryManager = InventoryManager.Instance();
        if (inventoryManager == null)
            return CreateEmptySnapshot(definition, "InventoryManager.Instance() returned null.");

        return CreateSummarySnapshot(
            definition,
            $"Pointer: {FormatPointer(inventoryManager)}",
            "InventoryManager summary snapshot.",
            new[]
            {
                SummaryRow("Pointer", FormatPointer(inventoryManager), ((nint)inventoryManager).ToString("X", CultureInfo.InvariantCulture), "Singleton address"),
                SummaryRow("NextContextId", inventoryManager->NextContextId, inventoryManager->NextContextId, "Next inventory operation context id"),
                SummaryRow("GetGil()", inventoryManager->GetGil(), inventoryManager->GetGil(), "Character gil"),
                SummaryRow("GetRetainerGil()", inventoryManager->GetRetainerGil(), inventoryManager->GetRetainerGil(), "Gil on the active retainer"),
                SummaryRow("GetFreeCompanyGil()", inventoryManager->GetFreeCompanyGil(), inventoryManager->GetFreeCompanyGil(), "Free Company chest gil"),
                SummaryRow("GetGoldSaucerCoin()", inventoryManager->GetGoldSaucerCoin(), inventoryManager->GetGoldSaucerCoin(), "MGP count"),
                SummaryRow("GetWolfMarks()", inventoryManager->GetWolfMarks(), inventoryManager->GetWolfMarks(), "Wolf Marks count"),
                SummaryRow("GetAlliedSeals()", inventoryManager->GetAlliedSeals(), inventoryManager->GetAlliedSeals(), "Allied Seals count"),
                SummaryRow("GetCompanySeals(1)", inventoryManager->GetCompanySeals(1), inventoryManager->GetCompanySeals(1), "Maelstrom seals"),
                SummaryRow("GetCompanySeals(2)", inventoryManager->GetCompanySeals(2), inventoryManager->GetCompanySeals(2), "Twin Adders seals"),
                SummaryRow("GetCompanySeals(3)", inventoryManager->GetCompanySeals(3), inventoryManager->GetCompanySeals(3), "Immortal Flames seals"),
                SummaryRow("GetEmptySlotsInBag()", inventoryManager->GetEmptySlotsInBag(), inventoryManager->GetEmptySlotsInBag(), "Empty inventory slots in player bags"),
                SummaryRow("GetPermittedGearsetCount()", inventoryManager->GetPermittedGearsetCount(), inventoryManager->GetPermittedGearsetCount(), "Allowed gearset count"),
                SummaryRow("TradeLocalState", inventoryManager->TradeLocalState.ToString(), inventoryManager->TradeLocalState, "Local trade state enum"),
                SummaryRow("TradeRemoteState", inventoryManager->TradeRemoteState.ToString(), inventoryManager->TradeRemoteState, "Remote trade state enum"),
                SummaryRow("TradePartnerName", inventoryManager->TradePartnerNameString, inventoryManager->TradePartnerNameString, "Current trade partner fixed string")
            });
    }

    private ClientStructsSheetSnapshot ReadInventorySlots(ClientStructsSheetDefinition definition, ClientStructsSheetRequest request)
    {
        var inventoryManager = InventoryManager.Instance();
        if (inventoryManager == null)
            return CreateEmptySnapshot(definition, "InventoryManager.Instance() returned null.");

        var container = inventoryManager->GetInventoryContainer(request.InventoryType);
        if (container == null)
            return CreateEmptySnapshot(definition, $"GetInventoryContainer({request.InventoryType}) returned null.");

        var totalRows = Math.Max(0, container->Size);
        var columns = CreateColumns(
            Column("Idx", "Index", "Visible inventory slot index", 56f),
            Column("Slot", "short", "Native InventoryItem.Slot", 62f),
            Column("ItemId", "uint", "Base item id in the slot", 84f),
            Column("Item", "Lumina Item", "Lumina-enriched item name when found", 220f),
            Column("Qty", "int", "Quantity in the slot", 70f),
            Column("Flags", "ItemFlags", "HighQuality / Collectable / etc.", 110f),
            Column("Condition%", "byte", "Durability percentage helper", 90f),
            Column("Spiritbond/Collectability", "ushort", "InventoryItem.SpiritbondOrCollectability", 140f),
            Column("Materia", "byte", "Resolved materia count", 70f),
            Column("GlamourId", "uint", "Resolved glamour id", 90f),
            Column("CrafterCid", "ulong", "Crafter content id", 120f));

        var visibleRows = new List<ClientStructsSheetRow>();
        BuildWindow(totalRows, request.StartIndex, request.RowCount, out var startIndex, out var rowsToLoad);
        for (var offset = 0; offset < rowsToLoad; offset++)
        {
            var rowIndex = startIndex + offset;
            var item = container->GetInventorySlot(rowIndex);
            if (item == null)
            {
                visibleRows.Add(CreateRow(
                    rowIndex,
                    (uint)rowIndex,
                    Cell(rowIndex),
                    Cell(string.Empty),
                    Cell(string.Empty),
                    Cell("<null>"),
                    Cell(string.Empty),
                    Cell(string.Empty),
                    Cell(string.Empty),
                    Cell(string.Empty),
                    Cell(string.Empty),
                    Cell(string.Empty)));
                continue;
            }

            var itemId = item->ItemId;
            var displayName = itemId == 0 ? "<empty>" : ResolveItemName(itemId);
            var flags = item->Flags.ToString();
            var condition = itemId == 0 ? string.Empty : item->GetConditionPercentage().ToString(CultureInfo.InvariantCulture);
            var materia = itemId == 0 ? string.Empty : item->GetMateriaCount().ToString(CultureInfo.InvariantCulture);
            visibleRows.Add(CreateRow(
                rowIndex,
                (uint)rowIndex,
                Cell(rowIndex),
                Cell(item->Slot),
                Cell(itemId),
                Cell(displayName, itemId),
                Cell(item->Quantity),
                Cell(flags, (byte)item->Flags),
                Cell(condition, item->Condition),
                Cell(item->SpiritbondOrCollectability),
                Cell(materia, item->GetMateriaCount()),
                Cell(item->GlamourId),
                Cell(item->CrafterContentId)));
        }

        var message = $"Rows {startIndex + 1}-{startIndex + visibleRows.Count} of {totalRows} • {request.InventoryType} • Loaded: {container->IsLoaded}";
        return CreateSnapshot(definition, columns, visibleRows, totalRows, startIndex, request.RowCount, $"Pointer: {FormatPointer(container)}", message);
    }

    private ClientStructsSheetSnapshot ReadRetainers(ClientStructsSheetDefinition definition, ClientStructsSheetRequest request)
    {
        var retainerManager = RetainerManager.Instance();
        if (retainerManager == null)
            return CreateEmptySnapshot(definition, "RetainerManager.Instance() returned null.");

        var count = retainerManager->GetRetainerCount();
        var columns = CreateColumns(
            Column("SortedIdx", "Index", "Display-order index", 80f),
            Column("RetainerId", "ulong", "Retainer content id", 130f),
            Column("Name", "string", "Generated fixed-string name accessor", 180f),
            Column("Available", "bool", "Availability flag", 75f),
            Column("ClassJob", "byte", "Retainer class/job id", 120f),
            Column("Level", "byte", "Retainer level", 62f),
            Column("Items", "byte", "Retainer inventory count", 62f),
            Column("Gil", "uint", "Retainer gil", 90f),
            Column("Town", "byte", "Raw town byte plus resolved city name when known", 140f),
            Column("MarketItems", "byte", "Active retainer market item count", 95f),
            Column("MarketExpire", "uint", "Retainer market expiry timestamp", 150f),
            Column("VentureId", "ushort", "Current venture id", 90f),
            Column("VentureComplete", "uint", "Current venture completion timestamp", 150f));

        var visibleRows = new List<ClientStructsSheetRow>();
        BuildWindow(count, request.StartIndex, request.RowCount, out var startIndex, out var rowsToLoad);
        for (var offset = 0; offset < rowsToLoad; offset++)
        {
            var rowIndex = startIndex + offset;
            var retainer = retainerManager->GetRetainerBySortedIndex((uint)rowIndex);
            if (retainer == null)
                continue;

            visibleRows.Add(CreateRow(
                rowIndex,
                (uint)rowIndex,
                Cell(rowIndex),
                Cell(retainer->RetainerId),
                Cell(retainer->NameString, retainer->RetainerId),
                Cell(retainer->Available),
                Cell(ResolveClassJobName(retainer->ClassJob), retainer->ClassJob),
                Cell(retainer->Level),
                Cell(retainer->ItemCount),
                Cell(retainer->Gil),
                Cell(FormatRetainerTown(retainer->Town), (byte)retainer->Town),
                Cell(retainer->MarketItemCount),
                Cell(FormatUnixTimestamp(retainer->MarketExpire), retainer->MarketExpire),
                Cell(retainer->VentureId),
                Cell(FormatUnixTimestamp(retainer->VentureComplete), retainer->VentureComplete)));
        }

        var message = $"Rows {startIndex + 1}-{startIndex + visibleRows.Count} of {count} • IsReady: {retainerManager->IsReady} • MaxEntitlement: {retainerManager->MaxRetainerEntitlement}";
        return CreateSnapshot(definition, columns, visibleRows, count, startIndex, request.RowCount, $"Pointer: {FormatPointer(retainerManager)}", message);
    }

    private ClientStructsSheetSnapshot ReadHousing(ClientStructsSheetDefinition definition)
    {
        var housingManager = HousingManager.Instance();
        if (housingManager == null)
            return CreateEmptySnapshot(definition, "HousingManager.Instance() returned null.");

        var currentHouse = housingManager->GetCurrentHouseId();
        var personalEstate = HousingManager.GetOwnedHouseId(EstateType.PersonalEstate);
        var apartment = HousingManager.GetOwnedHouseId(EstateType.ApartmentRoom);
        var freeCompanyEstate = HousingManager.GetOwnedHouseId(EstateType.FreeCompanyEstate);

        return CreateSummarySnapshot(
            definition,
            $"Pointer: {FormatPointer(housingManager)}",
            "HousingManager snapshot for current and owned-house state.",
            new[]
            {
                SummaryRow("Pointer", FormatPointer(housingManager), ((nint)housingManager).ToString("X", CultureInfo.InvariantCulture), "Singleton address"),
                SummaryRow("HasHousePermissions()", housingManager->HasHousePermissions(), housingManager->HasHousePermissions(), "Current housing permissions helper"),
                SummaryRow("IsOutside()", housingManager->IsOutside(), housingManager->IsOutside(), "Current housing outdoor state"),
                SummaryRow("IsInside()", housingManager->IsInside(), housingManager->IsInside(), "Current housing indoor state"),
                SummaryRow("IsInWorkshop()", housingManager->IsInWorkshop(), housingManager->IsInWorkshop(), "Current housing workshop state"),
                SummaryRow("CurrentWard", housingManager->GetCurrentWard(), housingManager->GetCurrentWard(), "Current ward helper"),
                SummaryRow("CurrentPlot", housingManager->GetCurrentPlot(), housingManager->GetCurrentPlot(), "Current plot helper"),
                SummaryRow("CurrentRoom", housingManager->GetCurrentRoom(), housingManager->GetCurrentRoom(), "Current room helper"),
                SummaryRow("CurrentDivision", housingManager->GetCurrentDivision(), housingManager->GetCurrentDivision(), "1 = main, 2 = subdivision"),
                SummaryRow("CurrentHouseId", FormatHouseId(currentHouse), currentHouse.Id, "Current HouseId breakdown"),
                SummaryRow("PersonalEstate", FormatHouseId(personalEstate), personalEstate.Id, "Owned personal estate HouseId"),
                SummaryRow("ApartmentRoom", FormatHouseId(apartment), apartment.Id, "Owned apartment room HouseId"),
                SummaryRow("FreeCompanyEstate", FormatHouseId(freeCompanyEstate), freeCompanyEstate.Id, "Owned Free Company estate HouseId")
            });
    }

    private ClientStructsSheetSnapshot ReadAgentMap(ClientStructsSheetDefinition definition)
    {
        var agentMap = TryGetAgentMap();
        if (agentMap == null)
            return CreateEmptySnapshot(definition, "AgentMap is not loaded.");

        return CreateSummarySnapshot(
            definition,
            $"Pointer: {FormatPointer(agentMap)}",
            "AgentMap summary snapshot.",
            new[]
            {
                SummaryRow("Pointer", FormatPointer(agentMap), ((nint)agentMap).ToString("X", CultureInfo.InvariantCulture), "AgentMap pointer"),
                SummaryRow("AddonId", agentMap->AddonId, agentMap->AddonId, "Backing addon id for the map agent"),
                SummaryRow("CurrentTerritoryId", ResolveTerritoryName(agentMap->CurrentTerritoryId), agentMap->CurrentTerritoryId, "Current territory tracked by AgentMap"),
                SummaryRow("CurrentMapId", agentMap->CurrentMapId, agentMap->CurrentMapId, "Current map id tracked by AgentMap"),
                SummaryRow("SelectedTerritoryId", ResolveTerritoryName(agentMap->SelectedTerritoryId), agentMap->SelectedTerritoryId, "Selected territory tracked by AgentMap"),
                SummaryRow("SelectedMapId", agentMap->SelectedMapId, agentMap->SelectedMapId, "Selected map id tracked by AgentMap"),
                SummaryRow("MapTitleString", agentMap->MapTitleString.ToString(), agentMap->MapTitleString.ToString(), "Displayed map title"),
                SummaryRow("CurrentMapPath", agentMap->CurrentMapPath.ToString(), agentMap->CurrentMapPath.ToString(), "Current map texture path"),
                SummaryRow("CurrentMapBgPath", agentMap->CurrentMapBgPath.ToString(), agentMap->CurrentMapBgPath.ToString(), "Current map background texture path"),
                SummaryRow("SelectedMapPath", agentMap->SelectedMapPath.ToString(), agentMap->SelectedMapPath.ToString(), "Selected map texture path"),
                SummaryRow("SelectedMapBgPath", agentMap->SelectedMapBgPath.ToString(), agentMap->SelectedMapBgPath.ToString(), "Selected map background texture path"),
                SummaryRow("CurrentMapSizeFactorFloat", agentMap->CurrentMapSizeFactorFloat.ToString("F3", CultureInfo.InvariantCulture), agentMap->CurrentMapSizeFactorFloat, "Current map size factor"),
                SummaryRow("SelectedMapSizeFactorFloat", agentMap->SelectedMapSizeFactorFloat.ToString("F3", CultureInfo.InvariantCulture), agentMap->SelectedMapSizeFactorFloat, "Selected map size factor"),
                SummaryRow("CurrentOffset", $"{agentMap->CurrentOffsetX}, {agentMap->CurrentOffsetY}", $"{agentMap->CurrentOffsetX},{agentMap->CurrentOffsetY}", "Current map x/y offset"),
                SummaryRow("SelectedOffset", $"{agentMap->SelectedOffsetX}, {agentMap->SelectedOffsetY}", $"{agentMap->SelectedOffsetX},{agentMap->SelectedOffsetY}", "Selected map x/y offset"),
                SummaryRow("CurrentMapMarkerRange", agentMap->CurrentMapMarkerRange, agentMap->CurrentMapMarkerRange, "Current map marker range"),
                SummaryRow("SelectedMapMarkerRange", agentMap->SelectedMapMarkerRange, agentMap->SelectedMapMarkerRange, "Selected map marker range"),
                SummaryRow("MapMarkerCount", agentMap->MapMarkerCount, agentMap->MapMarkerCount, "Active map-marker count"),
                SummaryRow("EventMarkerCount", agentMap->EventMarkers.Count, agentMap->EventMarkers.Count, "Dynamic event/FATE/runtime event-marker count"),
                SummaryRow("TempMapMarkerCount", agentMap->TempMapMarkerCount, agentMap->TempMapMarkerCount, "Temporary map-marker count"),
                SummaryRow("FlagMarkerCount", agentMap->FlagMarkerCount, agentMap->FlagMarkerCount, "Flag-marker count"),
                SummaryRow("MiniMapMarkerCount", agentMap->MiniMapMarkerCount, agentMap->MiniMapMarkerCount, "Minimap marker count"),
                SummaryRow("MapQuestLinkContainer.MarkerCount", agentMap->MapQuestLinkContainer.MarkerCount, agentMap->MapQuestLinkContainer.MarkerCount, "Quest-link markers attached to the main map"),
                SummaryRow("MiniMapQuestLinkContainer.MarkerCount", agentMap->MiniMapQuestLinkContainer.MarkerCount, agentMap->MiniMapQuestLinkContainer.MarkerCount, "Quest-link markers attached to the minimap"),
                SummaryRow("CurrentOpenMapInfo.Type", FormatEnumValue(agentMap->CurrentOpenMapInfo.Type), (uint)agentMap->CurrentOpenMapInfo.Type, "Current open-map type"),
                SummaryRow("CurrentOpenMapInfo.AddonId", agentMap->CurrentOpenMapInfo.AddonId, agentMap->CurrentOpenMapInfo.AddonId, "Current open-map addon id"),
                SummaryRow("CurrentOpenMapInfo.TerritoryId", ResolveTerritoryName(agentMap->CurrentOpenMapInfo.TerritoryId), agentMap->CurrentOpenMapInfo.TerritoryId, "Territory attached to the current open-map context"),
                SummaryRow("CurrentOpenMapInfo.MapId", agentMap->CurrentOpenMapInfo.MapId, agentMap->CurrentOpenMapInfo.MapId, "Map id attached to the current open-map context"),
                SummaryRow("CurrentOpenMapInfo.PlaceNameId", agentMap->CurrentOpenMapInfo.PlaceNameId, agentMap->CurrentOpenMapInfo.PlaceNameId, "PlaceName row id attached to the current open-map context"),
                SummaryRow("CurrentOpenMapInfo.FateId", agentMap->CurrentOpenMapInfo.FateId, agentMap->CurrentOpenMapInfo.FateId, "FATE id attached to the current open-map context"),
                SummaryRow("CurrentOpenMapInfo.QuestId", agentMap->CurrentOpenMapInfo.QuestId, agentMap->CurrentOpenMapInfo.QuestId, "Quest id attached to the current open-map context"),
                SummaryRow("IsPlayerMoving", agentMap->IsPlayerMoving, agentMap->IsPlayerMoving, "AgentMap movement flag"),
                SummaryRow("IsControlKeyPressed", agentMap->IsControlKeyPressed, agentMap->IsControlKeyPressed, "AgentMap control-key flag")
            });
    }

    private ClientStructsSheetSnapshot ReadMapMarkers(ClientStructsSheetDefinition definition, ClientStructsSheetRequest request)
    {
        var agentModule = AgentModule.Instance();
        if (agentModule == null)
            return CreateEmptySnapshot(definition, "AgentModule.Instance() returned null.");

        var agentMap = (AgentMap*)agentModule->GetAgentByInternalId(AgentId.Map);
        if (agentMap == null)
            return CreateEmptySnapshot(definition, "AgentMap is not loaded.");

        var markers = agentMap->MapMarkers;
        var totalRows = Math.Min((int)agentMap->MapMarkerCount, markers.Length);
        var columns = CreateColumns(
            Column("Idx", "Index", "Map-marker array index", 56f),
            Column("IconId", "uint", "Primary icon id", 84f),
            Column("SecondaryIconId", "uint", "Secondary icon id", 108f),
            Column("Scale", "int", "Marker scale", 72f),
            Column("X", "short", "Raw map X", 64f),
            Column("Y", "short", "Raw map Y", 64f),
            Column("DataType", "ushort", "Marker data type", 82f),
            Column("DataKey", "ushort", "Marker data key", 82f),
            Column("SubKey", "byte", "Marker subkey", 72f),
            Column("Subtext", "CStringPointer", "Marker subtext when present", 220f));

        var visibleRows = new List<ClientStructsSheetRow>();
        BuildWindow(totalRows, request.StartIndex, request.RowCount, out var startIndex, out var rowsToLoad);
        for (var offset = 0; offset < rowsToLoad; offset++)
        {
            var rowIndex = startIndex + offset;
            var marker = markers[rowIndex];
            visibleRows.Add(CreateRow(
                rowIndex,
                (uint)rowIndex,
                Cell(rowIndex),
                Cell(marker.MapMarker.IconId),
                Cell(marker.MapMarker.SecondaryIconId),
                Cell(marker.MapMarker.Scale),
                Cell(marker.MapMarker.X),
                Cell(marker.MapMarker.Y),
                Cell(marker.DataType),
                Cell(marker.DataKey),
                Cell(marker.MapMarkerSubKey),
                Cell(ReadCStringPointer(marker.MapMarker.Subtext), ReadCStringPointer(marker.MapMarker.Subtext))));
        }

        var message = $"Rows {startIndex + 1}-{startIndex + visibleRows.Count} of {totalRows} - AgentMap active map markers";
        return CreateSnapshot(definition, columns, visibleRows, totalRows, startIndex, request.RowCount, $"Pointer: {FormatPointer(agentMap)}", message);
    }

    private ClientStructsSheetSnapshot ReadEventMarkers(ClientStructsSheetDefinition definition, ClientStructsSheetRequest request)
    {
        var agentModule = AgentModule.Instance();
        if (agentModule == null)
            return CreateEmptySnapshot(definition, "AgentModule.Instance() returned null.");

        var agentMap = (AgentMap*)agentModule->GetAgentByInternalId(AgentId.Map);
        if (agentMap == null)
            return CreateEmptySnapshot(definition, "AgentMap is not loaded.");

        var markers = agentMap->EventMarkers;
        var totalRows = markers.Count;
        var columns = CreateColumns(
            Column("Idx", "Index", "Event-marker vector index", 56f),
            Column("IconId", "uint", "Marker icon id", 84f),
            Column("ObjectiveId", "uint", "Objective id", 92f),
            Column("RecommendedLevel", "ushort", "Recommended level", 110f),
            Column("MapId", "uint", "Map id", 84f),
            Column("Territory", "ushort", "TerritoryType row id", 180f),
            Column("MarkerType", "byte", "Marker type", 76f),
            Column("EventState", "sbyte", "Runtime event state", 76f),
            Column("Position", "Vector3", "World position", 180f),
            Column("Radius", "float", "Marker radius", 76f),
            Column("Tooltip", "Utf8String", "Tooltip text when present", 260f));

        var visibleRows = new List<ClientStructsSheetRow>();
        BuildWindow(totalRows, request.StartIndex, request.RowCount, out var startIndex, out var rowsToLoad);
        for (var offset = 0; offset < rowsToLoad; offset++)
        {
            var rowIndex = startIndex + offset;
            var marker = markers[rowIndex];
            visibleRows.Add(CreateRow(
                rowIndex,
                marker.ObjectiveId,
                Cell(rowIndex),
                Cell(marker.IconId),
                Cell(marker.ObjectiveId),
                Cell(marker.RecommendedLevel),
                Cell(marker.MapId),
                Cell(ResolveTerritoryName(marker.TerritoryTypeId), marker.TerritoryTypeId),
                Cell(marker.MarkerType),
                Cell(marker.EventState),
                Cell(FormatVector3(marker.Position), FormatVector3(marker.Position)),
                Cell(marker.Radius.ToString("F1", CultureInfo.InvariantCulture), marker.Radius),
                Cell(ReadUtf8String(marker.TooltipString), ReadUtf8String(marker.TooltipString))));
        }

        var message = $"Rows {startIndex + 1}-{startIndex + visibleRows.Count} of {totalRows} - AgentMap event markers";
        return CreateSnapshot(definition, columns, visibleRows, totalRows, startIndex, request.RowCount, $"Pointer: {FormatPointer(agentMap)}", message);
    }

    private ClientStructsSheetSnapshot ReadInfoModule(ClientStructsSheetDefinition definition)
    {
        var infoModule = InfoModule.Instance();
        if (infoModule == null)
            return CreateEmptySnapshot(definition, "InfoModule.Instance() returned null.");

        var linkshellAvailable = infoModule->GetInfoProxyById(InfoProxyId.Linkshell) != null;
        var crossWorldLinkshellAvailable = infoModule->GetInfoProxyById(InfoProxyId.CrossWorldLinkshell) != null;
        var itemSearchAvailable = infoModule->GetInfoProxyById(InfoProxyId.ItemSearch) != null;
        var freeCompanyAvailable = infoModule->GetInfoProxyById(InfoProxyId.FreeCompany) != null;

        return CreateSummarySnapshot(
            definition,
            $"Pointer: {FormatPointer(infoModule)}",
            "InfoModule snapshot and proxy availability summary.",
            new[]
            {
                SummaryRow("Pointer", FormatPointer(infoModule), ((nint)infoModule).ToString("X", CultureInfo.InvariantCulture), "Singleton address"),
                SummaryRow("LocalContentId", infoModule->GetLocalContentId(), infoModule->GetLocalContentId(), "InfoModule local content id helper"),
                SummaryRow("LocalCharName", infoModule->LocalCharName.ToString(), infoModule->LocalCharName.ToString(), "Utf8String local character name"),
                SummaryRow("OnlineStatusFlags", infoModule->OnlineStatusFlags, infoModule->OnlineStatusFlags, "Raw online status bitmask"),
                SummaryRow("IsInCrossWorldDuty()", infoModule->IsInCrossWorldDuty(), infoModule->IsInCrossWorldDuty(), "Cross-world duty helper"),
                SummaryRow("LinkshellProxyLoaded", linkshellAvailable, linkshellAvailable, "GetInfoProxyById(Linkshell) != null"),
                SummaryRow("CrossWorldLinkshellProxyLoaded", crossWorldLinkshellAvailable, crossWorldLinkshellAvailable, "GetInfoProxyById(CrossWorldLinkshell) != null"),
                SummaryRow("ItemSearchProxyLoaded", itemSearchAvailable, itemSearchAvailable, "GetInfoProxyById(ItemSearch) != null"),
                SummaryRow("FreeCompanyProxyLoaded", freeCompanyAvailable, freeCompanyAvailable, "GetInfoProxyById(FreeCompany) != null")
            });
    }

    private ClientStructsSheetSnapshot ReadLinkshell(ClientStructsSheetDefinition definition, ClientStructsSheetRequest request)
    {
        var infoModule = InfoModule.Instance();
        if (infoModule == null)
            return CreateEmptySnapshot(definition, "InfoModule.Instance() returned null.");

        var proxy = (InfoProxyLinkshell*)infoModule->GetInfoProxyById(InfoProxyId.Linkshell);
        if (proxy == null)
            return CreateEmptySnapshot(definition, "InfoProxyLinkshell is not loaded.");

        var selectedIndex = GetSelectedLinkshellIndex();
        const int totalRows = 8;
        var columns = CreateColumns(
            Column("Slot", "Index", "Zero-based Linkshell slot index", 60f),
            Column("Channel", "LS #", "User-facing Linkshell channel number", 72f),
            Column("Id", "ulong", "Linkshell id from InfoProxyLinkshell entry", 140f),
            Column("ChatId", "ulong", "Linkshell chat id from InfoProxyLinkshell entry", 140f),
            Column("Name", "CStringPointer", "Resolved Linkshell display name", 220f),
            Column("Flags", "uint", "Raw Linkshell flags field", 90f),
            Column("Active", "bool", "Whether this row matches ActiveLinkShellIndex", 72f),
            Column("Selected", "bool", "Whether this row matches AgentLinkshell.SelectedLSIndex", 82f));

        var visibleRows = new List<ClientStructsSheetRow>();
        BuildWindow(totalRows, request.StartIndex, request.RowCount, out var startIndex, out var rowsToLoad);
        for (var offset = 0; offset < rowsToLoad; offset++)
        {
            var rowIndex = startIndex + offset;
            var entry = proxy->GetLinkshellInfo((uint)rowIndex);
            if (entry == null)
                continue;

            var name = entry->Id == 0 ? string.Empty : proxy->GetLinkshellName(entry->Id).ToString();
            var isActive = proxy->ActiveLinkShellIndex == (uint)rowIndex;
            var isSelected = selectedIndex.HasValue && selectedIndex.Value == rowIndex;
            visibleRows.Add(CreateRow(
                rowIndex,
                (uint)(rowIndex + 1),
                Cell(rowIndex),
                Cell(rowIndex + 1),
                Cell(entry->Id),
                Cell(entry->ChatId),
                Cell(name, entry->Id),
                Cell(entry->Flags),
                Cell(isActive),
                Cell(isSelected)));
        }

        var message = $"Rows {startIndex + 1}-{startIndex + visibleRows.Count} of {totalRows} • ActiveLinkShellIndex: {proxy->ActiveLinkShellIndex} • SelectedLSIndex: {FormatOptionalIndex(selectedIndex)}";
        return CreateSnapshot(definition, columns, visibleRows, totalRows, startIndex, request.RowCount, $"Pointer: {FormatPointer(proxy)}", message);
    }

    private ClientStructsSheetSnapshot ReadCrossWorldLinkshell(ClientStructsSheetDefinition definition, ClientStructsSheetRequest request)
    {
        var infoModule = InfoModule.Instance();
        if (infoModule == null)
            return CreateEmptySnapshot(definition, "InfoModule.Instance() returned null.");

        var proxy = (InfoProxyCrossWorldLinkshell*)infoModule->GetInfoProxyById(InfoProxyId.CrossWorldLinkshell);
        if (proxy == null)
            return CreateEmptySnapshot(definition, "InfoProxyCrossWorldLinkshell is not loaded.");

        var selectedIndex = GetSelectedCrossWorldLinkshellIndex();
        const int totalRows = 8;
        var columns = CreateColumns(
            Column("Slot", "Index", "Zero-based Cross-world Linkshell slot index", 60f),
            Column("Channel", "CWLS #", "User-facing Cross-world Linkshell channel number", 82f),
            Column("Name", "Utf8String", "Cross-world Linkshell display name", 220f),
            Column("Membership", "ushort", "Membership type from the proxy entry", 100f),
            Column("FoundationTime", "uint", "Foundation timestamp from the proxy entry", 150f),
            Column("Selected", "bool", "Whether this row matches AgentCrossWorldLinkshell.SelectedCWLSIndex", 92f));

        var visibleRows = new List<ClientStructsSheetRow>();
        BuildWindow(totalRows, request.StartIndex, request.RowCount, out var startIndex, out var rowsToLoad);
        for (var offset = 0; offset < rowsToLoad; offset++)
        {
            var rowIndex = startIndex + offset;
            var name = proxy->GetCrossworldLinkshellName((uint)rowIndex);
            var entry = proxy->CrossWorldLinkshells[rowIndex];
            var isSelected = selectedIndex.HasValue && selectedIndex.Value == rowIndex;
            visibleRows.Add(CreateRow(
                rowIndex,
                (uint)(rowIndex + 1),
                Cell(rowIndex),
                Cell(rowIndex + 1),
                Cell(name == null ? string.Empty : name->ToString(), rowIndex + 1),
                Cell(FormatCrossWorldLinkshellMembershipType(entry.MembershipType), entry.MembershipType),
                Cell(FormatUnixTimestamp(entry.FoundationTime), entry.FoundationTime),
                Cell(isSelected)));
        }

        var message = $"Rows {startIndex + 1}-{startIndex + visibleRows.Count} of {totalRows} • NumInvites: {proxy->NumInvites} • SelectedCWLSIndex: {FormatOptionalIndex(selectedIndex)}";
        return CreateSnapshot(definition, columns, visibleRows, totalRows, startIndex, request.RowCount, $"Pointer: {FormatPointer(proxy)}", message);
    }

    private ClientStructsSheetSnapshot ReadItemSearchListings(ClientStructsSheetDefinition definition, ClientStructsSheetRequest request)
    {
        var infoModule = InfoModule.Instance();
        if (infoModule == null)
            return CreateEmptySnapshot(definition, "InfoModule.Instance() returned null.");

        var proxy = (InfoProxyItemSearch*)infoModule->GetInfoProxyById(InfoProxyId.ItemSearch);
        if (proxy == null)
            return CreateEmptySnapshot(definition, "InfoProxyItemSearch is not loaded.");

        var listings = proxy->Listings;
        var totalRows = Math.Min((int)proxy->ListingCount, listings.Length);
        var columns = CreateMarketListingColumns();
        var visibleRows = BuildMarketListingRows(listings, totalRows, request.StartIndex, request.RowCount);
        var requestStartIndex = Math.Clamp(request.StartIndex, 0, Math.Max(0, totalRows - Math.Clamp(request.RowCount, 1, 200)));
        var message = $"Rows {requestStartIndex + 1}-{requestStartIndex + visibleRows.Count} of {totalRows} • SearchItemId: {proxy->SearchItemId} • WaitingForListings: {proxy->WaitingForListings}";
        return CreateSnapshot(definition, columns, visibleRows, totalRows, requestStartIndex, request.RowCount, $"Pointer: {FormatPointer(proxy)}", message);
    }

    private ClientStructsSheetSnapshot ReadRetainerListings(ClientStructsSheetDefinition definition, ClientStructsSheetRequest request)
    {
        var infoModule = InfoModule.Instance();
        if (infoModule == null)
            return CreateEmptySnapshot(definition, "InfoModule.Instance() returned null.");

        var proxy = (InfoProxyItemSearch*)infoModule->GetInfoProxyById(InfoProxyId.ItemSearch);
        if (proxy == null)
            return CreateEmptySnapshot(definition, "InfoProxyItemSearch is not loaded.");

        var listings = proxy->RetainerListings;
        var totalRows = Math.Min((int)proxy->RetainerListingCount, listings.Length);
        var columns = CreateMarketListingColumns();
        var visibleRows = BuildMarketListingRows(listings, totalRows, request.StartIndex, request.RowCount);
        var requestStartIndex = Math.Clamp(request.StartIndex, 0, Math.Max(0, totalRows - Math.Clamp(request.RowCount, 1, 200)));
        var message = $"Rows {requestStartIndex + 1}-{requestStartIndex + visibleRows.Count} of {totalRows} • RetainerListingCount: {proxy->RetainerListingCount}";
        return CreateSnapshot(definition, columns, visibleRows, totalRows, requestStartIndex, request.RowCount, $"Pointer: {FormatPointer(proxy)}", message);
    }

    private ClientStructsSheetSnapshot ReadPlayerRetainers(ClientStructsSheetDefinition definition, ClientStructsSheetRequest request)
    {
        var infoModule = InfoModule.Instance();
        if (infoModule == null)
            return CreateEmptySnapshot(definition, "InfoModule.Instance() returned null.");

        var proxy = (InfoProxyItemSearch*)infoModule->GetInfoProxyById(InfoProxyId.ItemSearch);
        if (proxy == null)
            return CreateEmptySnapshot(definition, "InfoProxyItemSearch is not loaded.");

        var retainers = proxy->PlayerRetainers;
        var totalRows = Math.Min((int)proxy->PlayerRetainerCount, retainers.Length);
        var columns = CreateColumns(
            Column("Idx", "Index", "Visible player-retainer cache index", 56f),
            Column("RetainerId", "ulong", "Retainer id from market cache", 130f),
            Column("Name", "Utf8String", "Cached player retainer name", 180f),
            Column("TownId", "byte", "Raw town id from player-retainer cache", 80f),
            Column("SellingItems", "bool", "Whether the retainer is currently selling items", 95f));

        var visibleRows = new List<ClientStructsSheetRow>();
        BuildWindow(totalRows, request.StartIndex, request.RowCount, out var startIndex, out var rowsToLoad);
        for (var offset = 0; offset < rowsToLoad; offset++)
        {
            var rowIndex = startIndex + offset;
            var entry = retainers[rowIndex];
            visibleRows.Add(CreateRow(
                rowIndex,
                (uint)rowIndex,
                Cell(rowIndex),
                Cell(entry.RetainerId),
                Cell(entry.Name.ToString(), entry.RetainerId),
                Cell(entry.TownId),
                Cell(entry.SellingItems)));
        }

        var message = $"Rows {startIndex + 1}-{startIndex + visibleRows.Count} of {totalRows} • PlayerRetainerCount: {proxy->PlayerRetainerCount}";
        return CreateSnapshot(definition, columns, visibleRows, totalRows, startIndex, request.RowCount, $"Pointer: {FormatPointer(proxy)}", message);
    }

    private ClientStructsSheetSnapshot ReadFreeCompany(ClientStructsSheetDefinition definition)
    {
        var infoModule = InfoModule.Instance();
        if (infoModule == null)
            return CreateEmptySnapshot(definition, "InfoModule.Instance() returned null.");

        var freeCompany = (InfoProxyFreeCompany*)infoModule->GetInfoProxyById(InfoProxyId.FreeCompany);
        if (freeCompany == null)
            return CreateEmptySnapshot(definition, "InfoProxyFreeCompany is not loaded.");

        return CreateSummarySnapshot(
            definition,
            $"Pointer: {FormatPointer(freeCompany)}",
            "InfoProxyFreeCompany summary snapshot.",
            new[]
            {
                SummaryRow("Pointer", FormatPointer(freeCompany), ((nint)freeCompany).ToString("X", CultureInfo.InvariantCulture), "Proxy address"),
                SummaryRow("Id", freeCompany->Id, freeCompany->Id, "Free Company id"),
                SummaryRow("Name", freeCompany->NameString, freeCompany->NameString, "Generated fixed-string name accessor"),
                SummaryRow("Master", freeCompany->MasterString, freeCompany->MasterString, "Generated fixed-string master accessor"),
                SummaryRow("HomeWorldId", ResolveWorldName(freeCompany->HomeWorldId), freeCompany->HomeWorldId, "Home world resolved through Lumina when available"),
                SummaryRow("GrandCompany", freeCompany->GrandCompany.ToString(), freeCompany->GrandCompany, "Grand Company enum"),
                SummaryRow("Rank", freeCompany->Rank, freeCompany->Rank, "Free Company rank"),
                SummaryRow("OnlineMembers", freeCompany->OnlineMembers, freeCompany->OnlineMembers, "Current online member count"),
                SummaryRow("TotalMembers", freeCompany->TotalMembers, freeCompany->TotalMembers, "Total member count"),
                SummaryRow("ActiveListItemNum", freeCompany->ActiveListItemNum, freeCompany->ActiveListItemNum, "0 = Topics, 1 = Members, etc."),
                SummaryRow("MemberTabIndex", freeCompany->MemberTabIndex, freeCompany->MemberTabIndex, "Current member-tab index"),
                SummaryRow("InfoTabIndex", freeCompany->InfoTabIndex, freeCompany->InfoTabIndex, "Current info-tab index")
            });
    }

    private ClientStructsSheetSnapshot ReadFateSummary(ClientStructsSheetDefinition definition)
    {
        var fateManager = FateManager.Instance();
        if (fateManager == null)
            return CreateEmptySnapshot(definition, "FateManager.Instance() returned null.");

        ushort currentFateId = 0;
        try { currentFateId = fateManager->GetCurrentFateId(); } catch { }

        var currentFate = fateManager->CurrentFate;
        var fateDirector = fateManager->FateDirector;
        var agentModule = AgentModule.Instance();
        var agentFateProgress = agentModule == null ? null : (AgentFateProgress*)agentModule->GetAgentByInternalId(AgentId.FateProgress);

        var isSyncedToCurrent = false;
        try
        {
            isSyncedToCurrent = currentFate != null && fateManager->IsSyncedToFate(currentFate);
        }
        catch
        {
        }

        return CreateSummarySnapshot(
            definition,
            $"Pointer: {FormatPointer(fateManager)}",
            "FateManager summary snapshot.",
            new[]
            {
                SummaryRow("Pointer", FormatPointer(fateManager), ((nint)fateManager).ToString("X", CultureInfo.InvariantCulture), "FateManager singleton address"),
                SummaryRow("Fates.Count", fateManager->Fates.Count, fateManager->Fates.Count, "Number of active FateContext pointers"),
                SummaryRow("FateJoined", fateManager->FateJoined, fateManager->FateJoined, "Joined/current fate flag"),
                SummaryRow("SyncedFateId", fateManager->SyncedFateId, fateManager->SyncedFateId, "Currently synced fate id"),
                SummaryRow("GetCurrentFateId()", currentFateId, currentFateId, "Current fate id helper"),
                SummaryRow("CurrentFate.Pointer", FormatPointer(currentFate), currentFate == null ? "0" : ((nint)currentFate).ToString("X", CultureInfo.InvariantCulture), "Current FateContext pointer"),
                SummaryRow("CurrentFate.Name", currentFate == null ? string.Empty : currentFate->Name.ToString(), currentFate == null ? string.Empty : currentFate->Name.ToString(), "Current fate name"),
                SummaryRow("CurrentFate.State", currentFate == null ? "<none>" : FormatEnumValue(currentFate->State), currentFate == null ? 0 : (byte)currentFate->State, "Current fate runtime state"),
                SummaryRow("CurrentFate.Progress", currentFate == null ? 0 : currentFate->Progress, currentFate == null ? 0 : currentFate->Progress, "Current fate progress"),
                SummaryRow("CurrentFate.Level", currentFate == null ? 0 : currentFate->Level, currentFate == null ? 0 : currentFate->Level, "Current fate level"),
                SummaryRow("CurrentFate.Bonus", currentFate != null && currentFate->IsBonus, currentFate != null && currentFate->IsBonus, "Bonus-fate flag"),
                SummaryRow("CurrentFate.Eureka", currentFate != null && currentFate->EurekaFate != 0, currentFate != null && currentFate->EurekaFate != 0, "Eureka-fate flag"),
                SummaryRow("CurrentFate.Location", currentFate == null ? string.Empty : FormatVector3(currentFate->Location), currentFate == null ? string.Empty : FormatVector3(currentFate->Location), "Current fate center position"),
                SummaryRow("CurrentFate.Radius", currentFate == null ? "0" : currentFate->Radius.ToString("F1", CultureInfo.InvariantCulture), currentFate == null ? 0 : currentFate->Radius, "Current fate radius"),
                SummaryRow("IsSyncedToCurrentFate", isSyncedToCurrent, isSyncedToCurrent, "Whether the local player is currently synced to the active fate"),
                SummaryRow("FateDirector.Pointer", FormatPointer(fateDirector), fateDirector == null ? "0" : ((nint)fateDirector).ToString("X", CultureInfo.InvariantCulture), "FateDirector pointer"),
                SummaryRow("FateDirector.FateId", fateDirector == null ? 0 : fateDirector->FateId, fateDirector == null ? 0 : fateDirector->FateId, "FateDirector fate id"),
                SummaryRow("FateDirector.FateLevel", fateDirector == null ? 0 : fateDirector->FateLevel, fateDirector == null ? 0 : fateDirector->FateLevel, "FateDirector fate level"),
                SummaryRow("FateDirector.FateNpcObjectId", fateDirector == null ? 0 : fateDirector->FateNpcObjectId, fateDirector == null ? 0 : fateDirector->FateNpcObjectId, "FateDirector NPC object id"),
                SummaryRow("AgentFateProgress.TabIndex", agentFateProgress == null ? 0 : agentFateProgress->TabIndex, agentFateProgress == null ? 0 : agentFateProgress->TabIndex, "Selected shared-fate tab when the Fate Progress agent is loaded")
            });
    }

    private ClientStructsSheetSnapshot ReadActiveFates(ClientStructsSheetDefinition definition, ClientStructsSheetRequest request)
    {
        var fateManager = FateManager.Instance();
        if (fateManager == null)
            return CreateEmptySnapshot(definition, "FateManager.Instance() returned null.");

        var activeFates = new List<(int VectorIndex, nint FatePointer)>();
        for (var i = 0; i < fateManager->Fates.Count; i++)
        {
            var fate = fateManager->Fates[i].Value;
            if (fate == null || fate->FateId == 0)
                continue;

            activeFates.Add((i, (nint)fate));
        }

        var columns = CreateColumns(
            Column("VecIdx", "Index", "FateManager.Fates vector index", 62f),
            Column("FateId", "ushort", "FATE id", 76f),
            Column("State", "enum", "FATE runtime state", 110f),
            Column("Name", "Utf8String", "Runtime fate name", 220f),
            Column("Level", "byte", "FATE level", 64f),
            Column("MaxLevel", "byte", "Maximum sync level", 76f),
            Column("Progress", "byte", "Progress percentage", 72f),
            Column("Bonus", "bool", "Bonus-fate flag", 64f),
            Column("Eureka", "bool", "Eureka-fate flag", 70f),
            Column("StartTime", "int", "FATE start timestamp", 150f),
            Column("DurationMin", "short", "FATE duration in minutes", 90f),
            Column("RequiredQuest", "Quest", "Required quest row id", 180f),
            Column("Location", "Vector3", "FATE center position", 180f),
            Column("Radius", "float", "FATE radius", 76f));

        var visibleRows = new List<ClientStructsSheetRow>();
        BuildWindow(activeFates.Count, request.StartIndex, request.RowCount, out var startIndex, out var rowsToLoad);
        for (var offset = 0; offset < rowsToLoad; offset++)
        {
            var rowIndex = startIndex + offset;
            var entry = activeFates[rowIndex];
            var fate = (FateContext*)entry.FatePointer;
            visibleRows.Add(CreateRow(
                rowIndex,
                fate->FateId,
                Cell(entry.VectorIndex),
                Cell(fate->FateId),
                Cell(FormatEnumValue(fate->State), (byte)fate->State),
                Cell(fate->Name.ToString(), fate->Name.ToString()),
                Cell(fate->Level),
                Cell(fate->MaxLevel),
                Cell(fate->Progress),
                Cell(fate->IsBonus),
                Cell(fate->EurekaFate != 0),
                Cell(FormatUnixTimestamp(fate->StartTimeEpoch), fate->StartTimeEpoch),
                Cell(fate->Duration),
                Cell(ResolveQuestRowName(fate->RequiredQuest), fate->RequiredQuest),
                Cell(FormatVector3(fate->Location), FormatVector3(fate->Location)),
                Cell(fate->Radius.ToString("F1", CultureInfo.InvariantCulture), fate->Radius)));
        }

        var message = $"Rows {startIndex + 1}-{startIndex + visibleRows.Count} of {activeFates.Count} - Active FATE contexts";
        return CreateSnapshot(definition, columns, visibleRows, activeFates.Count, startIndex, request.RowCount, $"Pointer: {FormatPointer(fateManager)}", message);
    }

    private ClientStructsSheetSnapshot ReadPartyMembers(ClientStructsSheetDefinition definition, ClientStructsSheetRequest request)
    {
        var groupManager = GroupManager.Instance();
        if (groupManager == null)
            return CreateEmptySnapshot(definition, "GroupManager.Instance() returned null.");

        var group = groupManager->GetGroup();
        if (group == null)
            return CreateEmptySnapshot(definition, "GroupManager has no active group.");

        var members = new List<(int Slot, bool AllianceEntry, nint MemberPointer)>();
        if (group->IsAlliance)
        {
            for (var i = 0; i < 20; i++)
            {
                var member = group->GetAllianceMemberByIndex(i);
                if (member == null || member->ContentId == 0)
                    continue;

                members.Add((i, true, (nint)member));
            }
        }
        else
        {
            for (var i = 0; i < 8; i++)
            {
                var member = group->GetPartyMemberByIndex(i);
                if (member == null || member->ContentId == 0)
                    continue;

                members.Add((i, false, (nint)member));
            }
        }

        var columns = CreateColumns(
            Column("Slot", "Index", "Party/alliance slot index", 56f),
            Column("Alliance", "bool", "True when the row came from alliance indexing", 72f),
            Column("ContentId", "ulong", "Character content id", 140f),
            Column("EntityId", "uint", "Character entity id", 110f),
            Column("Name", "string", "Party-member display name", 180f),
            Column("HomeWorld", "ushort", "Home world row id", 140f),
            Column("Territory", "ushort", "Current territory row id", 180f),
            Column("ClassJob", "byte", "Current class/job id", 120f),
            Column("Level", "byte", "Current level", 64f),
            Column("HP", "uint", "Current and max HP", 110f),
            Column("MP", "ushort", "Current and max MP", 110f),
            Column("Position", "Vector3", "Current world position", 180f),
            Column("Flags", "byte", "Raw party-member flags byte", 72f),
            Column("Cv2", "uint", "Content value key 2", 72f),
            Column("Cv3", "uint", "Content value key 3", 72f),
            Column("Cv4", "uint", "Content value key 4", 72f));

        var visibleRows = new List<ClientStructsSheetRow>();
        BuildWindow(members.Count, request.StartIndex, request.RowCount, out var startIndex, out var rowsToLoad);
        for (var offset = 0; offset < rowsToLoad; offset++)
        {
            var rowIndex = startIndex + offset;
            var entry = members[rowIndex];
            var member = (PartyMember*)entry.MemberPointer;
            var name = member->NameOverride != null ? member->NameOverride->ToString() : member->NameString;
            visibleRows.Add(CreateRow(
                rowIndex,
                (uint)entry.Slot,
                Cell(entry.Slot),
                Cell(entry.AllianceEntry),
                Cell(member->ContentId),
                Cell(member->EntityId),
                Cell(name, member->ContentId),
                Cell(ResolveWorldName(member->HomeWorld), member->HomeWorld),
                Cell(ResolveTerritoryName(member->TerritoryType), member->TerritoryType),
                Cell(ResolveClassJobName(member->ClassJob), member->ClassJob),
                Cell(member->Level),
                Cell($"{member->CurrentHP}/{member->MaxHP}", $"{member->CurrentHP}/{member->MaxHP}"),
                Cell($"{member->CurrentMP}/{member->MaxMP}", $"{member->CurrentMP}/{member->MaxMP}"),
                Cell(FormatVector3(member->Position), FormatVector3(member->Position)),
                Cell(member->Flags),
                Cell(member->GetContentValue(2)),
                Cell(member->GetContentValue(3)),
                Cell(member->GetContentValue(4))));
        }

        var message = $"Rows {startIndex + 1}-{startIndex + visibleRows.Count} of {members.Count} - MemberCount: {group->MemberCount} - PartyLeaderIndex: {group->PartyLeaderIndex} - IsAlliance: {group->IsAlliance} - IsSmallGroupAlliance: {group->IsSmallGroupAlliance}";
        return CreateSnapshot(definition, columns, visibleRows, members.Count, startIndex, request.RowCount, $"Pointer: {FormatPointer(group)}", message);
    }

    private ClientStructsSheetSnapshot ReadActionManager(ClientStructsSheetDefinition definition)
    {
        var actionManager = ActionManager.Instance();
        if (actionManager == null)
            return CreateEmptySnapshot(definition, "ActionManager.Instance() returned null.");

        return CreateSummarySnapshot(
            definition,
            $"Pointer: {FormatPointer(actionManager)}",
            "ActionManager snapshot for cast and queue state.",
            new[]
            {
                SummaryRow("Pointer", FormatPointer(actionManager), ((nint)actionManager).ToString("X", CultureInfo.InvariantCulture), "Singleton address"),
                SummaryRow("AnimationLock", actionManager->AnimationLock.ToString("F3", CultureInfo.InvariantCulture), actionManager->AnimationLock, "Current animation lock"),
                SummaryRow("CastSpellId", actionManager->CastSpellId, actionManager->CastSpellId, "Resolved spell id for the current cast"),
                SummaryRow("CastActionType", actionManager->CastActionType.ToString(), actionManager->CastActionType, "Current cast action type"),
                SummaryRow("CastActionId", actionManager->CastActionId, actionManager->CastActionId, "Current cast action id"),
                SummaryRow("CastTimeElapsed", actionManager->CastTimeElapsed.ToString("F3", CultureInfo.InvariantCulture), actionManager->CastTimeElapsed, "Current cast elapsed time"),
                SummaryRow("CastTimeTotal", actionManager->CastTimeTotal.ToString("F3", CultureInfo.InvariantCulture), actionManager->CastTimeTotal, "Current cast total time"),
                SummaryRow("CastTargetId", actionManager->CastTargetId.ToString(), actionManager->CastTargetId.ToString(), "Current cast target object id"),
                SummaryRow("ActionQueued", actionManager->ActionQueued, actionManager->ActionQueued, "Whether an action is currently queued"),
                SummaryRow("QueuedActionType", actionManager->QueuedActionType.ToString(), actionManager->QueuedActionType, "Queued action type"),
                SummaryRow("QueuedActionId", actionManager->QueuedActionId, actionManager->QueuedActionId, "Queued action id"),
                SummaryRow("QueuedTargetId", actionManager->QueuedTargetId.ToString(), actionManager->QueuedTargetId.ToString(), "Queued target id"),
                SummaryRow("AreaTargetingActionId", actionManager->AreaTargetingActionId, actionManager->AreaTargetingActionId, "Area-targeting action id"),
                SummaryRow("AreaTargetingActionType", actionManager->AreaTargetingActionType.ToString(), actionManager->AreaTargetingActionType, "Area-targeting action type"),
                SummaryRow("BallistaActive", actionManager->BallistaActive, actionManager->BallistaActive, "Ballista mode active"),
                SummaryRow("DistanceToTargetHitbox", actionManager->DistanceToTargetHitbox.ToString("F2", CultureInfo.InvariantCulture), actionManager->DistanceToTargetHitbox, "Distance to target minus hitbox radii")
            });
    }

    private ClientStructsSheetSnapshot ReadTargetSystem(ClientStructsSheetDefinition definition)
    {
        var targetSystem = TargetSystem.Instance();
        if (targetSystem == null)
            return CreateEmptySnapshot(definition, "TargetSystem.Instance() returned null.");

        return CreateSummarySnapshot(
            definition,
            $"Pointer: {FormatPointer(targetSystem)}",
            "TargetSystem snapshot for current targeting state.",
            new[]
            {
                SummaryRow("Pointer", FormatPointer(targetSystem), ((nint)targetSystem).ToString("X", CultureInfo.InvariantCulture), "Singleton address"),
                SummaryRow("TargetModeIndex", targetSystem->TargetModeIndex, targetSystem->TargetModeIndex, "Current target-mode slot"),
                SummaryRow("Target", FormatPointer(targetSystem->Target), FormatPointer(targetSystem->Target), "Raw hard target pointer field"),
                SummaryRow("SoftTarget", FormatPointer(targetSystem->SoftTarget), FormatPointer(targetSystem->SoftTarget), "Raw soft target pointer field"),
                SummaryRow("FocusTarget", FormatPointer(targetSystem->FocusTarget), FormatPointer(targetSystem->FocusTarget), "Raw focus target pointer field"),
                SummaryRow("MouseOverTarget", FormatPointer(targetSystem->MouseOverTarget), FormatPointer(targetSystem->MouseOverTarget), "Raw mouseover target pointer field"),
                SummaryRow("PreviousTarget", FormatPointer(targetSystem->PreviousTarget), FormatPointer(targetSystem->PreviousTarget), "Raw previous target pointer field"),
                SummaryRow("GetTargetObjectId()", targetSystem->GetTargetObjectId().ToString(), targetSystem->GetTargetObjectId().ToString(), "Resolved target object id helper"),
                SummaryRow("GetTargetObject()", FormatPointer(targetSystem->GetTargetObject()), FormatPointer(targetSystem->GetTargetObject()), "Resolved target object helper"),
                SummaryRow("GetHardTarget()", FormatPointer(targetSystem->GetHardTarget()), FormatPointer(targetSystem->GetHardTarget()), "Resolved hard target helper"),
                SummaryRow("GetSoftTarget()", FormatPointer(targetSystem->GetSoftTarget()), FormatPointer(targetSystem->GetSoftTarget()), "Resolved soft target helper"),
                SummaryRow("TargetCircle.WeaponDrawn", targetSystem->TargetCircleManager.WeaponDrawn, targetSystem->TargetCircleManager.WeaponDrawn, "Target-circle weapon state"),
                SummaryRow("TargetCircle.CyclingEnabled", targetSystem->TargetCircleManager.CyclingEnabled, targetSystem->TargetCircleManager.CyclingEnabled, "Target-cycle enabled state"),
                SummaryRow("TargetCircle.CustomFilterEnabled", targetSystem->TargetCircleManager.CustomFilterEnabled, targetSystem->TargetCircleManager.CustomFilterEnabled, "Custom target filter enabled state")
            });
    }

    private static List<ClientStructsSheetColumn> CreateMarketListingColumns()
        => CreateColumns(
            Column("Idx", "Index", "Visible market listing index", 56f),
            Column("ListingId", "ulong", "Market listing id", 130f),
            Column("ItemId", "uint", "Item row id", 84f),
            Column("Item", "Lumina Item", "Lumina-enriched item name when found", 220f),
            Column("Qty", "uint", "Listing quantity", 70f),
            Column("UnitPrice", "uint", "Unit price", 90f),
            Column("TotalTax", "uint", "Total tax", 90f),
            Column("HQ", "bool", "High quality flag", 56f),
            Column("TownId", "byte", "Town id", 70f),
            Column("ContainerIndex", "ushort", "Retainer market container index", 95f),
            Column("Materia", "byte", "Materia count", 70f),
            Column("Durability", "ushort", "Item durability", 90f),
            Column("Spiritbond", "ushort", "Item spiritbond", 90f),
            Column("RetainerId", "ulong", "Selling retainer id", 140f),
            Column("ContentId", "ulong", "Seller/player content id", 140f));

    private List<ClientStructsSheetRow> BuildMarketListingRows(Span<MarketBoardListing> listings, int totalRows, int requestedStartIndex, int requestedRowCount)
    {
        var visibleRows = new List<ClientStructsSheetRow>();
        BuildWindow(totalRows, requestedStartIndex, requestedRowCount, out var startIndex, out var rowsToLoad);
        for (var offset = 0; offset < rowsToLoad; offset++)
        {
            var rowIndex = startIndex + offset;
            var entry = listings[rowIndex];
            visibleRows.Add(CreateRow(
                rowIndex,
                (uint)rowIndex,
                Cell(rowIndex),
                Cell(entry.ListingId),
                Cell(entry.ItemId),
                Cell(ResolveItemName(entry.ItemId), entry.ItemId),
                Cell(entry.Quantity),
                Cell(entry.UnitPrice),
                Cell(entry.TotalTax),
                Cell(entry.IsHqItem),
                Cell(entry.TownId),
                Cell(entry.ContainerIndex),
                Cell(entry.MateriaCount),
                Cell(entry.Durability),
                Cell(entry.Spiritbond),
                Cell(entry.RetainerId),
                Cell(entry.ContentId)));
        }

        return visibleRows;
    }

    private static ClientStructsSheetSnapshot CreateSummarySnapshot(ClientStructsSheetDefinition definition, string status, string message, IEnumerable<ClientStructsSummaryRow> summaryRows)
    {
        var columns = CreateColumns(
            Column("Field", "Field", "ClientStructs field or helper", 220f),
            Column("Value", "Display", "Formatted display value", 240f),
            Column("Raw", "Raw", "Raw value or backing id", 180f),
            Column("Notes", "Notes", "Why this field matters", 280f));

        var rows = summaryRows.Select((row, index) => CreateRow(
            index,
            (uint)(index + 1),
            Cell(row.Field, row.Field),
            Cell(row.Value, row.Raw),
            Cell(row.Raw, row.Raw),
            Cell(row.Notes, row.Notes))).ToList();

        return CreateSnapshot(definition, columns, rows, rows.Count, 0, rows.Count, status, message);
    }

    private static ClientStructsSheetSnapshot CreateSnapshot(
        ClientStructsSheetDefinition definition,
        List<ClientStructsSheetColumn> columns,
        List<ClientStructsSheetRow> rows,
        int totalRowCount,
        int startIndex,
        int rowCount,
        string status,
        string message)
    {
        return new ClientStructsSheetSnapshot
        {
            Definition = definition,
            Status = status,
            Message = message,
            TotalRowCount = totalRowCount,
            StartIndex = startIndex,
            RowCount = rowCount,
            Columns = columns,
            Rows = rows,
            VisibleRowsCopyText = BuildVisibleRowsCopyText(columns, rows)
        };
    }

    private static ClientStructsSheetSnapshot CreateEmptySnapshot(ClientStructsSheetDefinition definition, string message)
        => new()
        {
            Definition = definition,
            Status = message,
            Message = message,
            TotalRowCount = 0,
            StartIndex = 0,
            RowCount = 0,
            Columns = new List<ClientStructsSheetColumn>(),
            Rows = new List<ClientStructsSheetRow>(),
            VisibleRowsCopyText = string.Empty
        };

    private static string ResolveTerritoryName(uint territoryId)
    {
        if (territoryId == 0)
            return "0";

        try
        {
            var sheet = Plugin.DataManager.GetExcelSheet<TerritoryType>();
            if (sheet == null || !sheet.TryGetRow(territoryId, out var row))
                return territoryId.ToString(CultureInfo.InvariantCulture);

            var placeName = row.PlaceName.ValueNullable?.Name.ToString()?.Trim() ?? string.Empty;
            return string.IsNullOrWhiteSpace(placeName)
                ? territoryId.ToString(CultureInfo.InvariantCulture)
                : $"{territoryId} - {placeName}";
        }
        catch
        {
            return territoryId.ToString(CultureInfo.InvariantCulture);
        }
    }

    private static string ResolveContentFinderConditionName(ushort contentId)
    {
        if (contentId == 0)
            return "0";

        try
        {
            var sheet = Plugin.DataManager.GetExcelSheet<ContentFinderCondition>();
            if (sheet == null || !sheet.TryGetRow(contentId, out var row))
                return contentId.ToString(CultureInfo.InvariantCulture);

            var name = row.Name.ToString().Trim();
            return string.IsNullOrWhiteSpace(name)
                ? contentId.ToString(CultureInfo.InvariantCulture)
                : $"{contentId} - {name}";
        }
        catch
        {
            return contentId.ToString(CultureInfo.InvariantCulture);
        }
    }

    private static void BuildWindow(int totalRows, int requestedStartIndex, int requestedRowCount, out int startIndex, out int rowsToLoad)
    {
        var clampedRowCount = Math.Clamp(requestedRowCount, 5, 100);
        startIndex = Math.Clamp(requestedStartIndex, 0, Math.Max(0, totalRows - clampedRowCount));
        rowsToLoad = Math.Min(clampedRowCount, Math.Max(0, totalRows - startIndex));
    }

    private static ClientStructsSheetColumn Column(string header, string descriptor, string tooltip, float width)
        => new()
        {
            Header = header,
            Descriptor = descriptor,
            Tooltip = tooltip,
            Width = width
        };

    private static List<ClientStructsSheetColumn> CreateColumns(params ClientStructsSheetColumn[] columns)
    {
        for (var i = 0; i < columns.Length; i++)
            columns[i].ColumnIndex = i;
        return columns.ToList();
    }

    private static ClientStructsSheetRow CreateRow(int rowIndex, uint rowId, params ClientStructsSheetCell[] cells)
    {
        for (var i = 0; i < cells.Length; i++)
            cells[i].ColumnIndex = i;
        return new ClientStructsSheetRow
        {
            RowIndex = rowIndex,
            RowId = rowId,
            Cells = cells.ToList()
        };
    }

    private static ClientStructsSheetCell Cell(object? value, object? rawValue = null)
    {
        var display = value switch
        {
            null => string.Empty,
            bool b => b ? "True" : "False",
            float f => f.ToString("F3", CultureInfo.InvariantCulture),
            double d => d.ToString("F3", CultureInfo.InvariantCulture),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
        };
        var raw = rawValue switch
        {
            null => display,
            bool b => b ? "True" : "False",
            _ => Convert.ToString(rawValue, CultureInfo.InvariantCulture) ?? display
        };
        return new ClientStructsSheetCell
        {
            DisplayText = display,
            RawText = raw
        };
    }

    private static ClientStructsSummaryRow SummaryRow(string field, object? value, object? raw, string notes)
        => new(field, Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty, Convert.ToString(raw, CultureInfo.InvariantCulture) ?? string.Empty, notes);

    private static uint ResolvePreferredInstanceCandidate(
        uint publicInstanceId,
        short proxyCurrentInstance,
        short networkCurrentInstance,
        uint dalamudClientStateInstance,
        ZoneInstanceSnapshot zoneInit,
        ContentsReplayManager* replayManager,
        bool hasReplayZoneInit,
        out string source)
    {
        if (publicInstanceId != 0)
        {
            source = "UIState.PublicInstance.InstanceId";
            return publicInstanceId;
        }

        if (proxyCurrentInstance > 0)
        {
            source = "NetworkModuleProxy.GetCurrentInstance()";
            return (uint)proxyCurrentInstance;
        }

        if (networkCurrentInstance > 0)
        {
            source = "NetworkModule.CurrentInstance";
            return (uint)networkCurrentInstance;
        }

        if (dalamudClientStateInstance != 0)
        {
            source = "Dalamud.ClientState.Instance";
            return dalamudClientStateInstance;
        }

        if (zoneInit.HasCapturedPacket && zoneInit.PacketInstance != 0)
        {
            source = "LastZoneInit.PacketInstance";
            return zoneInit.PacketInstance;
        }

        if (hasReplayZoneInit && replayManager != null && replayManager->ZoneInitPacket.Instance != 0)
        {
            source = "ReplayZoneInit.Instance";
            return replayManager->ZoneInitPacket.Instance;
        }

        source = "None";
        return 0;
    }

    private static ushort ResolvePreferredServerIdCandidate(ZoneInstanceSnapshot zoneInit, ContentsReplayManager* replayManager, bool hasReplayZoneInit, out string source)
    {
        if (zoneInit.HasCapturedPacket && zoneInit.ServerId != 0)
        {
            source = "LastZoneInit.ServerId";
            return zoneInit.ServerId;
        }

        if (hasReplayZoneInit && replayManager != null && replayManager->ZoneInitPacket.ServerId != 0)
        {
            source = "ReplayZoneInit.ServerId";
            return replayManager->ZoneInitPacket.ServerId;
        }

        source = "None";
        return 0;
    }

    private static uint ResolvePreferredPopRangeCandidate(ZoneInstanceSnapshot zoneInit, ContentsReplayManager* replayManager, bool hasReplayZoneInit, PublicContentDirector* publicContentDirector, out string source)
    {
        if (zoneInit.HasCapturedPacket && zoneInit.PopRangeId != 0)
        {
            source = "LastZoneInit.PopRangeId";
            return zoneInit.PopRangeId;
        }

        if (hasReplayZoneInit && replayManager != null && replayManager->ZoneInitPacket.PopRangeId != 0)
        {
            source = "ReplayZoneInit.PopRangeId";
            return replayManager->ZoneInitPacket.PopRangeId;
        }

        if (publicContentDirector != null && publicContentDirector->LGBPopRange != 0)
        {
            source = "PublicContent.LGBPopRange";
            return publicContentDirector->LGBPopRange;
        }

        source = "None";
        return 0;
    }

    private static string BuildVisibleRowsCopyText(IReadOnlyList<ClientStructsSheetColumn> columns, IReadOnlyList<ClientStructsSheetRow> rows)
    {
        if (columns.Count == 0 || rows.Count == 0)
            return string.Empty;

        var lines = new List<string>();
        var header = new List<string> { "RowIndex", "RowId" };
        header.AddRange(columns.Select(x => x.Header));
        lines.Add(string.Join("\t", header));

        foreach (var row in rows)
        {
            var cells = new List<string>
            {
                row.RowIndex.ToString(CultureInfo.InvariantCulture),
                row.RowId.ToString(CultureInfo.InvariantCulture)
            };
            cells.AddRange(row.Cells.Select(x => x.DisplayText.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ')));
            lines.Add(string.Join("\t", cells));
        }

        return string.Join("\n", lines);
    }

    private static string FormatPointer(void* pointer)
        => pointer == null ? "<null>" : $"0x{((nint)pointer):X}";

    private static string FormatPointer<T>(T* pointer) where T : unmanaged
        => pointer == null ? "<null>" : $"0x{((nint)pointer):X}";

    private static string FormatUnixTimestamp(long value)
    {
        if (value <= 0)
            return value.ToString(CultureInfo.InvariantCulture);

        try
        {
            return $"{value} => {DateTimeOffset.FromUnixTimeSeconds(value).LocalDateTime:yyyy-MM-dd HH:mm:ss}";
        }
        catch
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }
    }

    private static string FormatUnixTimestamp(uint value)
        => FormatUnixTimestamp((long)value);

    private static int? GetSelectedLinkshellIndex()
    {
        var agentModule = AgentModule.Instance();
        if (agentModule == null)
            return null;

        var agent = (AgentLinkshell*)agentModule->GetAgentByInternalId(AgentId.Linkshell);
        return agent == null ? null : agent->SelectedLSIndex;
    }

    private static int? GetSelectedCrossWorldLinkshellIndex()
    {
        var agentModule = AgentModule.Instance();
        if (agentModule == null)
            return null;

        var agent = (AgentCrossWorldLinkshell*)agentModule->GetAgentByInternalId(AgentId.CrossWorldLinkShell);
        return agent == null ? null : agent->SelectedCWLSIndex;
    }

    private static string FormatOptionalIndex(int? value)
        => value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : "<unavailable>";

    private static string FormatEnumValue<TEnum>(TEnum value) where TEnum : struct, Enum
    {
        var raw = Convert.ToUInt64(value, CultureInfo.InvariantCulture);
        return Enum.IsDefined(value)
            ? $"{raw} ({value})"
            : raw.ToString(CultureInfo.InvariantCulture);
    }

    private static string FormatTerritoryIntendedUse(ClientTerritoryIntendedUse value)
        => FormatEnumValue(value);

    private static string FormatContentType(EventContentType value)
        => FormatEnumValue(value);

    private static string FormatPublicContentDirectorType(PublicContentDirectorType value)
        => FormatEnumValue(value);

    private static string FormatZoneInitFlags(ZoneInitFlags value)
    {
        var raw = (ushort)value;
        return raw == 0
            ? "0 (None)"
            : $"{raw} ({value})";
    }

    private static string FormatCrossWorldLinkshellMembershipType(ushort membershipType)
        => membershipType switch
        {
            0 => "0 — Invitee",
            1 => "1 — Member",
            2 => "2 — Leader",
            3 => "3 — Master",
            _ => membershipType.ToString(CultureInfo.InvariantCulture)
        };

    private static string FormatRetainerTown(RetainerManager.RetainerTown town)
    {
        var townId = (byte)town;
        var townName = townId switch
        {
            1 => "Limsa Lominsa",
            2 => "Gridania",
            3 => "Ul'dah",
            4 => "Ishgard",
            7 => "Kugane",
            10 => "Crystarium",
            12 => "Old Sharlayan",
            14 => "Tuliyollal",
            _ => string.Empty
        };

        return string.IsNullOrWhiteSpace(townName)
            ? townId.ToString(CultureInfo.InvariantCulture)
            : $"{townId} ({townName})";
    }

    private static string FormatHouseId(HouseId houseId)
        => houseId.Id == 0
            ? "0"
            : $"{houseId.Id} => Territory:{houseId.TerritoryTypeId}, World:{houseId.WorldId}, Ward:{houseId.WardIndex}, Plot:{houseId.PlotIndex}, Room:{houseId.RoomNumber}, Apartment:{houseId.IsApartment}";

    private static string FormatVector3(System.Numerics.Vector3 value)
        => $"{value.X:F1}, {value.Y:F1}, {value.Z:F1}";

    private static string ReadUtf8String(Utf8String* value)
    {
        if (value == null)
            return string.Empty;

        try
        {
            return value->ToString() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string ReadCStringPointer(CStringPointer value)
    {
        try
        {
            return value.ToString() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string ResolveItemName(uint itemId)
    {
        if (itemId == 0)
            return string.Empty;

        try
        {
            var sheet = Plugin.DataManager.GetExcelSheet<Item>();
            return sheet != null && sheet.TryGetRow(itemId, out var row)
                ? $"{itemId} — {row.Name}"
                : itemId.ToString(CultureInfo.InvariantCulture);
        }
        catch
        {
            return itemId.ToString(CultureInfo.InvariantCulture);
        }
    }

    private static string ResolveWeatherName(byte weatherId)
    {
        if (weatherId == 0)
            return "0";

        try
        {
            var sheet = Plugin.DataManager.GetExcelSheet<Weather>();
            return sheet != null && sheet.TryGetRow(weatherId, out var row)
                ? $"{weatherId} - {row.Name}"
                : weatherId.ToString(CultureInfo.InvariantCulture);
        }
        catch
        {
            return weatherId.ToString(CultureInfo.InvariantCulture);
        }
    }

    private static string ResolveClassJobName(byte classJobId)
    {
        if (classJobId == 0)
            return "0";

        try
        {
            var sheet = Plugin.DataManager.GetExcelSheet<ClassJob>();
            return sheet != null && sheet.TryGetRow(classJobId, out var row)
                ? $"{classJobId} — {row.Name}"
                : classJobId.ToString(CultureInfo.InvariantCulture);
        }
        catch
        {
            return classJobId.ToString(CultureInfo.InvariantCulture);
        }
    }

    private static string ResolveQuestName(ushort questId)
    {
        if (questId == 0)
            return string.Empty;

        try
        {
            var sheet = Plugin.DataManager.GetExcelSheet<Quest>();
            var rowId = (uint)questId + 65536u;
            return sheet != null && sheet.TryGetRow(rowId, out var row)
                ? $"{questId} — {row.Name}"
                : questId.ToString(CultureInfo.InvariantCulture);
        }
        catch
        {
            return questId.ToString(CultureInfo.InvariantCulture);
        }
    }

    private static string ResolveQuestRowName(uint questRowId)
    {
        if (questRowId == 0)
            return "0";

        try
        {
            var sheet = Plugin.DataManager.GetExcelSheet<Quest>();
            return sheet != null && sheet.TryGetRow(questRowId, out var row)
                ? $"{questRowId} - {row.Name}"
                : questRowId.ToString(CultureInfo.InvariantCulture);
        }
        catch
        {
            return questRowId.ToString(CultureInfo.InvariantCulture);
        }
    }

    private static string ResolveWorldName(ushort worldId)
    {
        if (worldId == 0)
            return "0";

        try
        {
            var sheet = Plugin.DataManager.GetExcelSheet<World>();
            return sheet != null && sheet.TryGetRow(worldId, out var row)
                ? $"{worldId} — {row.Name}"
                : worldId.ToString(CultureInfo.InvariantCulture);
        }
        catch
        {
            return worldId.ToString(CultureInfo.InvariantCulture);
        }
    }

    private static string ResolveContentRouletteName(uint rouletteId)
    {
        if (rouletteId == 0)
            return "0";

        try
        {
            var sheet = Plugin.DataManager.GetExcelSheet<LuminaContentRouletteSheet>();
            return sheet != null && sheet.TryGetRow(rouletteId, out var row)
                ? $"{rouletteId} - {row.Name}"
                : rouletteId.ToString(CultureInfo.InvariantCulture);
        }
        catch
        {
            return rouletteId.ToString(CultureInfo.InvariantCulture);
        }
    }

    private static AgentMap* TryGetAgentMap()
    {
        var agentModule = AgentModule.Instance();
        return agentModule == null ? null : (AgentMap*)agentModule->GetAgentByInternalId(AgentId.Map);
    }

    private static string ResolveQueueEntry(ContentsId entry)
        => entry.ContentType switch
        {
            AgentContentsType.None => "None",
            AgentContentsType.Regular => $"Regular - {ResolveContentFinderConditionName((ushort)entry.Id)}",
            AgentContentsType.Roulette => $"Roulette - {ResolveContentRouletteName(entry.Id)}",
            _ => $"{entry.ContentType} - Id={entry.Id}"
        };
}

public sealed record ClientStructsSheetDefinition(
    string Name,
    string Category,
    string Description,
    string AccessSnippet,
    bool SupportsWindowing = false,
    bool SupportsInventoryType = false);

public sealed class ClientStructsSheetRequest
{
    public int StartIndex { get; set; }
    public int RowCount { get; set; } = 20;
    public InventoryType InventoryType { get; set; } = InventoryType.Inventory1;
}

public sealed class ClientStructsSheetSnapshot
{
    public ClientStructsSheetDefinition Definition { get; set; } = null!;
    public string Status { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public int TotalRowCount { get; set; }
    public int StartIndex { get; set; }
    public int RowCount { get; set; }
    public List<ClientStructsSheetColumn> Columns { get; set; } = new();
    public List<ClientStructsSheetRow> Rows { get; set; } = new();
    public string VisibleRowsCopyText { get; set; } = string.Empty;
}

public sealed class ClientStructsSheetColumn
{
    public int ColumnIndex { get; set; }
    public string Header { get; set; } = string.Empty;
    public string Descriptor { get; set; } = string.Empty;
    public string Tooltip { get; set; } = string.Empty;
    public float Width { get; set; }
}

public sealed class ClientStructsSheetCell
{
    public int ColumnIndex { get; set; }
    public string DisplayText { get; set; } = string.Empty;
    public string RawText { get; set; } = string.Empty;
}

public sealed class ClientStructsSheetRow
{
    public int RowIndex { get; set; }
    public uint RowId { get; set; }
    public List<ClientStructsSheetCell> Cells { get; set; } = new();
}

public sealed class ClientStructsSearchResult
{
    public string SheetName { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public int RowIndex { get; set; } = -1;
    public uint RowId { get; set; }
    public int ColumnIndex { get; set; } = -1;
    public string ColumnHeader { get; set; } = string.Empty;
    public string MatchSource { get; set; } = string.Empty;
    public string DisplayText { get; set; } = string.Empty;
    public string RawText { get; set; } = string.Empty;
    public string DetailText { get; set; } = string.Empty;
}

public sealed record ClientStructsSummaryRow(string Field, string Value, string Raw, string Notes);
