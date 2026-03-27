using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using FFXIVClientStructs.FFXIV.Application.Network.WorkDefinitions;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using Lumina.Excel.Sheets;

namespace XAHudNavigator.Services;

public sealed unsafe class ClientStructsSheetService
{
    private static readonly IReadOnlyList<ClientStructsSheetDefinition> SheetDefinitions = new[]
    {
        new ClientStructsSheetDefinition("GameMain", "Game", "Live zone, map, territory, and content state.", "var gm = GameMain.Instance();"),
        new ClientStructsSheetDefinition("PlayerState", "Player", "Current character identity, level, unlock, and weekly state.", "var ps = PlayerState.Instance();"),
        new ClientStructsSheetDefinition("UIState", "Player", "UI-backed runtime state such as item level and buddy timers.", "var uiState = UIState.Instance();"),
        new ClientStructsSheetDefinition("Quest Summary", "Quests", "QuestManager summary counts and timers.", "var qm = QuestManager.Instance();"),
        new ClientStructsSheetDefinition("Active Quests", "Quests", "Accepted normal quests from QuestManager.NormalQuests.", "var quests = QuestManager.Instance()->NormalQuests;", SupportsWindowing: true),
        new ClientStructsSheetDefinition("Daily Quests", "Quests", "Accepted daily quests from QuestManager.DailyQuests.", "var dailies = QuestManager.Instance()->DailyQuests;", SupportsWindowing: true),
        new ClientStructsSheetDefinition("Leves", "Quests", "Accepted leves from QuestManager.LeveQuests.", "var leves = QuestManager.Instance()->LeveQuests;", SupportsWindowing: true),
        new ClientStructsSheetDefinition("Inventory Summary", "Inventory", "Currency and bag state from InventoryManager.", "var im = InventoryManager.Instance();"),
        new ClientStructsSheetDefinition("Inventory Slots", "Inventory", "Windowed browse of a selected InventoryType container.", "var container = InventoryManager.Instance()->GetInventoryContainer(InventoryType.Inventory1);", SupportsWindowing: true, SupportsInventoryType: true),
        new ClientStructsSheetDefinition("Retainers", "Retainers", "Retainer roster and venture summary data from RetainerManager.", "var rm = RetainerManager.Instance();", SupportsWindowing: true),
        new ClientStructsSheetDefinition("Housing", "Housing", "Current and owned-house state from HousingManager.", "var hm = HousingManager.Instance();"),
        new ClientStructsSheetDefinition("InfoModule", "Social/UI", "InfoModule and InfoProxy availability state.", "var infoModule = InfoModule.Instance();"),
        new ClientStructsSheetDefinition("Linkshell", "Social/UI", "Normal Linkshell slots from InfoProxyLinkshell.", "var linkshell = (InfoProxyLinkshell*)InfoModule.Instance()->GetInfoProxyById(InfoProxyId.Linkshell);", SupportsWindowing: true),
        new ClientStructsSheetDefinition("Cross-world Linkshell", "Social/UI", "Cross-world Linkshell slots from InfoProxyCrossWorldLinkshell.", "var cwls = (InfoProxyCrossWorldLinkshell*)InfoModule.Instance()->GetInfoProxyById(InfoProxyId.CrossWorldLinkshell);", SupportsWindowing: true),
        new ClientStructsSheetDefinition("Item Search Listings", "Market", "General marketboard listings cached in InfoProxyItemSearch.", "var itemSearch = (InfoProxyItemSearch*)InfoModule.Instance()->GetInfoProxyById(InfoProxyId.ItemSearch);", SupportsWindowing: true),
        new ClientStructsSheetDefinition("Retainer Listings", "Market", "Last targeted retainer listings cached in InfoProxyItemSearch.", "var itemSearch = (InfoProxyItemSearch*)InfoModule.Instance()->GetInfoProxyById(InfoProxyId.ItemSearch);", SupportsWindowing: true),
        new ClientStructsSheetDefinition("Player Retainers", "Market", "Player retainer market cache from InfoProxyItemSearch.", "var itemSearch = (InfoProxyItemSearch*)InfoModule.Instance()->GetInfoProxyById(InfoProxyId.ItemSearch);", SupportsWindowing: true),
        new ClientStructsSheetDefinition("Free Company", "Social/UI", "Free Company summary state from InfoProxyFreeCompany.", "var freeCompany = (InfoProxyFreeCompany*)InfoModule.Instance()->GetInfoProxyById(InfoProxyId.FreeCompany);"),
        new ClientStructsSheetDefinition("ActionManager", "Automation", "Current cast, queue, and targeting state from ActionManager.", "var am = ActionManager.Instance();"),
        new ClientStructsSheetDefinition("TargetSystem", "Automation", "Current hard/soft/focus target state from TargetSystem.", "var ts = TargetSystem.Instance();")
    };

    public IReadOnlyList<ClientStructsSheetDefinition> Definitions => SheetDefinitions;

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
            "Quest Summary" => ReadQuestSummary(definition),
            "Active Quests" => ReadActiveQuests(definition, request),
            "Daily Quests" => ReadDailyQuests(definition, request),
            "Leves" => ReadLeves(definition, request),
            "Inventory Summary" => ReadInventorySummary(definition),
            "Inventory Slots" => ReadInventorySlots(definition, request),
            "Retainers" => ReadRetainers(definition, request),
            "Housing" => ReadHousing(definition),
            "InfoModule" => ReadInfoModule(definition),
            "Linkshell" => ReadLinkshell(definition, request),
            "Cross-world Linkshell" => ReadCrossWorldLinkshell(definition, request),
            "Item Search Listings" => ReadItemSearchListings(definition, request),
            "Retainer Listings" => ReadRetainerListings(definition, request),
            "Player Retainers" => ReadPlayerRetainers(definition, request),
            "Free Company" => ReadFreeCompany(definition),
            "ActionManager" => ReadActionManager(definition),
            "TargetSystem" => ReadTargetSystem(definition),
            _ => CreateEmptySnapshot(definition, $"{definition.Name} is not implemented.")
        };
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
                SummaryRow("NextTerritoryTypeId", gameMain->NextTerritoryTypeId, gameMain->NextTerritoryTypeId, "Incoming territory type row id"),
                SummaryRow("CurrentMapId", gameMain->CurrentMapId, gameMain->CurrentMapId, "Current map row id"),
                SummaryRow("CurrentContentFinderConditionId", gameMain->CurrentContentFinderConditionId, gameMain->CurrentContentFinderConditionId, "Current duty/content finder row id"),
                SummaryRow("CurrentTerritoryIntendedUseId", gameMain->CurrentTerritoryIntendedUseId.ToString(), (uint)gameMain->CurrentTerritoryIntendedUseId, "Live intended-use enum"),
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
                SummaryRow("GMRank", uiState->GMRank, uiState->GMRank, "GM rank / debug state byte")
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
            Column("SellingRetainerCid", "ulong", "Selling retainer content id", 140f),
            Column("SellingPlayerCid", "ulong", "Selling player content id", 140f));

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
                Cell(entry.SellingRetainerContentId),
                Cell(entry.SellingPlayerContentId)));
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

public sealed record ClientStructsSummaryRow(string Field, string Value, string Raw, string Notes);
