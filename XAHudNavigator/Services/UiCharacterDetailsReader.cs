using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace XAHudNavigator.Services;

/// <summary>Copies current-owner character fields and verified profile node pairs into managed rows.</summary>
internal static unsafe class UiCharacterDetailsReader
{
    internal const int MaxProfileNodes = 512;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly (string Label, uint LabelId, uint ValueId)[] ProfilePairs =
    [
        ("Title", 3, 6), ("Race / Clan / Gender", 18, 20), ("City-state", 22, 25),
        ("Nameday", 27, 29), ("Guardian", 31, 34),
    ];

    internal readonly record struct Sources(nint Player, nint Character = 0, nint UiState = 0);

    public static UiMenuSnapshot? Capture(string name, AtkUnitBase* addon)
    {
        if (name is not ("CharacterStatus" or "CharacterProfile")) return null;
        return CaptureWithSources(name, addon,
            () => new((nint)PlayerState.Instance(), Plugin.ObjectTable.LocalPlayer?.Address ?? 0,
                name == "CharacterStatus" ? (nint)UIState.Instance() : 0), Lookup);
    }

    internal static UiMenuSnapshot CaptureWithSources(string name, AtkUnitBase* addon,
        Func<Sources> getSources, Func<string, uint, string?>? lookup = null)
    {
        if (name is not ("CharacterStatus" or "CharacterProfile"))
            return Unavailable(name, addon, "This addon has no verified character details mapping.");
        try
        {
            if (!TryAddon((nint)addon, out var initialAddon))
                return Unavailable(name, addon, "The selected addon is unreadable, hidden or not ready.");
            var sources = getSources();
            if (!TryPlayer(sources.Player, out var player))
                return Unavailable(name, addon, "The current player is unreadable, unloaded or has no owner identity.");
            var hasCharacter = TryCharacter(sources.Character, player, out var character);
            ushort itemLevel = 0;
            var hasUiState = name == "CharacterStatus" && TryItemLevel(sources.UiState, player, out itemLevel);
            var entries = new List<UiMenuEntry>();
            var issues = new List<string>();
            var nodes = new List<TextStamp>();
            var nodeIdentities = new List<NodeIdentity>();
            if (name == "CharacterStatus")
                ReadStatus(player, hasCharacter ? character : null, hasUiState ? itemLevel : null, entries, issues);
            else
            {
                if (!ReadProfileNodes(initialAddon, entries, nodes, nodeIdentities, issues))
                    return Unavailable(name, addon, "The profile node list is malformed or unreadable; no paired values were published.");
                ReadProfileFields(player, hasCharacter ? character : null, lookup, entries, issues);
            }

            var finalSources = getSources();
            if (sources != finalSources || !TryAddon((nint)addon, out var finalAddon)
                || initialAddon.Id != finalAddon.Id || initialAddon.ParentId != finalAddon.ParentId
                || !TryPlayer(finalSources.Player, out var finalPlayer) || !SamePlayer(player, finalPlayer))
                return Unavailable(name, addon, "The selected addon or current player changed during capture; all character rows were discarded.");
            if (hasCharacter && (!TryCharacter(finalSources.Character, finalPlayer, out var afterCharacter)
                || !SameCharacter(character, afterCharacter)))
                return Unavailable(name, addon, "The local character changed during capture; all character rows were discarded.");
            if (hasUiState && (!TryItemLevel(finalSources.UiState, finalPlayer, out var afterItemLevel)
                || itemLevel != afterItemLevel))
                return Unavailable(name, addon, "The current item level changed during capture; all character rows were discarded.");
            if (name == "CharacterProfile" && (initialAddon.UldManager.NodeList != finalAddon.UldManager.NodeList
                || initialAddon.UldManager.NodeListCount != finalAddon.UldManager.NodeListCount
                || nodeIdentities.Any(node => !VerifyIdentity(node))
                || nodes.Any(node => !VerifyNode(node))))
                return Unavailable(name, addon, "The profile node identity or text changed during capture; all paired rows were discarded.");
            return new(name, (ulong)(nuint)addon, "Character", name == "CharacterStatus" ? "Character status" : "Character profile", entries.ToArray())
            {
                CapturedAtUtc = DateTime.UtcNow, IsVisible = true, IsDecoded = true, IsPartial = issues.Count != 0,
                Status = issues.Count == 0 ? "Copied current-player values with verified owner and addon identity."
                    : string.Join(" ", issues.Distinct(StringComparer.Ordinal)),
                Fields = [new("Source", "Current player; copied native fields and verified profile node IDs.", "HUD Navigator character reader"),
                    new("Capture boundary", "Bounded observation; independently copied values are not an atomic game snapshot.", "HUD Navigator interpretation")],
            };
        }
        catch (Exception ex)
        {
            return Unavailable(name, addon, $"Character details are unavailable ({ex.GetType().Name}); no rows were published.");
        }
    }

    private static void ReadStatus(PlayerState player, Character? character, ushort? itemLevel,
        List<UiMenuEntry> entries, List<string> issues)
    {
        if (character is { } local)
        {
            AddResource("HP", local.Health, local.MaxHealth, "Health", "MaxHealth");
            AddResource("MP", local.Mana, local.MaxMana, "Mana", "MaxMana");
            AddResource("GP", local.GatheringPoints, local.MaxGatheringPoints, "GatheringPoints", "MaxGatheringPoints");
            AddResource("CP", local.CraftingPoints, local.MaxCraftingPoints, "CraftingPoints", "MaxCraftingPoints");
        }
        else
        {
            foreach (var label in new[] { "HP", "MP", "GP", "CP" })
                Add(entries, label, "Unavailable: local character was not verified", "Current resources", "Local Character owner/entity verification", "unavailable");
            issues.Add("Current HP/MP/GP/CP unavailable: a matching local character could not be copied.");
        }
        Add(entries, "Average item level", itemLevel.HasValue ? Number(itemLevel.Value) : "Unavailable", "Current resources",
            "UIState.CurrentItemLevel; embedded PlayerState owner verified", itemLevel.HasValue ? Number(itemLevel.Value) : "unavailable");
        if (!itemLevel.HasValue) issues.Add("Average item level unavailable: matching UIState fields could not be copied.");
        foreach (var attribute in Enum.GetValues<PlayerAttribute>())
        {
            var index = (int)attribute;
            if (index < 0 || index >= player.Attributes.Length)
                throw new InvalidOperationException("PlayerAttribute exceeds the installed inline array capacity.");
            var value = Number(player.Attributes[index]);
            Add(entries, AttributeLabel(attribute), value, AttributeGroup(attribute), $"PlayerState.Attributes[{index}] ({attribute})", value,
                new("Attribute ID", Number(index), "FFXIVClientStructs.PlayerAttribute"),
                new("Attribute enum", attribute.ToString(), "FFXIVClientStructs.PlayerAttribute"));
        }
        AddBase("Strength", player.BaseStrength); AddBase("Dexterity", player.BaseDexterity);
        AddBase("Vitality", player.BaseVitality); AddBase("Intelligence", player.BaseIntelligence);
        AddBase("Mind", player.BaseMind); AddBase("Piety", player.BasePiety);
        return;

        void AddResource(string label, uint current, uint maximum, string currentField, string maximumField)
            => Add(entries, label, $"{Number(current)} / {Number(maximum)}", "Current resources",
                $"Character.CharacterData.{currentField} / {maximumField}; local owner and entity verified",
                $"current={Number(current)}; maximum={Number(maximum)}",
                new("Current", Number(current), $"Character.CharacterData.{currentField}"),
                new("Maximum", Number(maximum), $"Character.CharacterData.{maximumField}"));
        void AddBase(string name, int value)
            => Add(entries, "Base " + name.ToLowerInvariant(), Number(value), "Base attributes", "PlayerState.Base" + name, Number(value));
    }

    internal static string AttributeLabel(PlayerAttribute attribute) => attribute switch
    {
        PlayerAttribute.HealthPoints => "HP (attribute)", PlayerAttribute.MagicPoints => "MP (attribute)",
        PlayerAttribute.TacticalPoints => "TP (attribute)", PlayerAttribute.GatheringPoints => "GP (attribute)",
        PlayerAttribute.CraftingPoints => "CP (attribute)", PlayerAttribute.EXPBonus => "EXP bonus",
        _ => FriendlyName(attribute.ToString()),
    };

    private static string AttributeGroup(PlayerAttribute attribute) => (int)attribute switch
    {
        >= 1 and <= 6 => "Main attributes", >= 7 and <= 11 => "Resource attributes",
        19 or 20 or 21 or 22 or 24 or 27 or 33 or 34 or 44 or 45 or 46 => "Combat attributes",
        70 or 71 => "Crafting attributes", 72 or 73 => "Gathering attributes", _ => "Additional attributes",
    };

    private static string FriendlyName(string name)
    {
        var result = new StringBuilder();
        for (var i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i]) && (char.IsLower(name[i - 1])
                || i + 1 < name.Length && char.IsLower(name[i + 1]))) result.Append(' ');
            result.Append(i == 0 ? name[i] : char.ToLowerInvariant(name[i]));
        }
        return result.ToString();
    }

    private static void ReadProfileFields(PlayerState player, Character? character,
        Func<string, uint, string?>? lookup, List<UiMenuEntry> entries, List<string> issues)
    {
        if (player.GrandCompany <= 3)
            AddSheet("Grand Company", player.GrandCompany, "GrandCompany.Name", "PlayerState.GrandCompany", player.GrandCompany == 0);
        else
        {
            Add(entries, "Grand Company", "Unavailable: unsupported company", "Profile", "PlayerState.GrandCompany", Number(player.GrandCompany));
            issues.Add("Grand Company is outside the verified mapping; its raw ID is retained.");
        }
        if (player.GrandCompany is >= 1 and <= 3 && player.Sex <= 1)
        {
            var company = player.GrandCompany switch { 1 => "Limsa", 2 => "Gridania", _ => "Uldah" };
            var sheet = $"GCRank{company}{(player.Sex == 0 ? "Male" : "Female")}Text.Singular";
            AddSheet("Grand Company rank", player.GCRanks[player.GrandCompany - 1], sheet,
                $"PlayerState.GCRanks[{player.GrandCompany - 1}]; GrandCompany={player.GrandCompany}; Sex={player.Sex}", false);
        }
        else
        {
            var none = player.GrandCompany == 0;
            Add(entries, "Grand Company rank", none ? "Not enlisted" : "Unavailable: unsupported company or sex", "Profile",
                "PlayerState.GrandCompany / Sex", $"company={player.GrandCompany}; sex={player.Sex}");
            if (!none) issues.Add("Grand Company rank unavailable: company or sex is outside the verified mapping.");
        }
        var sexSuffix = player.Sex == 0 ? "Masculine" : "Feminine";
        if (player.Sex <= 1)
        {
            AddSheet("Race", player.Race, "Race." + sexSuffix, "PlayerState.Race / Sex", false);
            AddSheet("Clan", player.Tribe, "Tribe." + sexSuffix, "PlayerState.Tribe / Sex", false);
        }
        else
        {
            Add(entries, "Race", "Unavailable: unsupported sex", "Profile", "PlayerState.Race / Sex", Number(player.Race));
            Add(entries, "Clan", "Unavailable: unsupported sex", "Profile", "PlayerState.Tribe / Sex", Number(player.Tribe));
            issues.Add("Race/clan names unavailable: sex is outside the verified sheet mapping.");
        }
        Add(entries, "Gender", player.Sex switch { 0 => "Male", 1 => "Female", _ => "Unavailable" }, "Profile",
            "PlayerState.Sex; HUD Navigator labels for 0=Male, 1=Female", Number(player.Sex));
        var tag = character is { } local ? ReadTag(local) : null;
        Add(entries, "Free Company", string.IsNullOrEmpty(tag) ? "Unavailable: full company name has no verified local source"
            : $"Name unavailable; local company tag: {tag}", "Profile", "Character.FreeCompanyTag; full name unavailable",
            tag ?? "unavailable", new UiMenuField("Local company tag", tag ?? "Unavailable", "Copied current-owner Character.FreeCompanyTag"));
        issues.Add("Full Free Company name unavailable; a local tag, when present, is identified separately.");
        return;

        void AddSheet(string label, uint id, string sheet, string nativeSource, bool none)
        {
            string? text = null;
            try { if (!none && id != 0) text = lookup?.Invoke(sheet, id); }
            catch (Exception) { /* A missing sheet cannot turn a raw ID into a claimed name. */ }
            var available = !string.IsNullOrWhiteSpace(text);
            var value = none ? "Not enlisted" : available ? text! : $"Unavailable (ID {id})";
            Add(entries, label, value, "Profile", available ? $"{nativeSource}; Lumina {sheet}, row {id}" : nativeSource, Number(id));
            if (!none && !available) issues.Add($"{label} name unavailable; its raw ID is retained.");
        }
    }

    private static bool ReadProfileNodes(AtkUnitBase addon, List<UiMenuEntry> entries, List<TextStamp> stamps,
        List<NodeIdentity> identities, List<string> issues)
    {
        var count = addon.UldManager.NodeListCount;
        var list = (nint)addon.UldManager.NodeList;
        if (count > MaxProfileNodes || count != 0 && list == 0) return false;
        var wanted = ProfilePairs.SelectMany(pair => new[] { pair.LabelId, pair.ValueId }).ToHashSet();
        var found = new Dictionary<uint, TextStamp>();
        var duplicates = new HashSet<uint>();
        for (var index = 0; index < count; index++)
        {
            if (!TryOffset(list, index * sizeof(nint), out var cell) || !NativeSheetReader.TryCopy(cell, out nint address)) return false;
            if (address == 0) { identities.Add(new(cell, address, 0, default)); continue; }
            if (!NativeSheetReader.TryCopy(address, out AtkResNode node)) return false;
            identities.Add(new(cell, address, node.NodeId, node.Type));
            if (!wanted.Contains(node.NodeId)) continue;
            var stamp = ReadNode(cell, address, node.NodeId);
            stamps.Add(stamp);
            if (!found.TryAdd(node.NodeId, stamp)) duplicates.Add(node.NodeId);
        }
        foreach (var pair in ProfilePairs)
        {
            found.TryGetValue(pair.LabelId, out var label);
            found.TryGetValue(pair.ValueId, out var value);
            var reason = duplicates.Contains(pair.LabelId) || duplicates.Contains(pair.ValueId) ? "duplicate node ID"
                : label is null || value is null ? "label or value node missing"
                : label.Issue.Length != 0 ? "label: " + label.Issue : value.Issue;
            var source = $"CharacterProfile.UldManager.NodeList: label node #{pair.LabelId}, value node #{pair.ValueId}";
            Add(entries, pair.Label, reason.Length == 0 ? value!.Text : "Unavailable: " + reason, "Profile", source,
                value?.RawHex ?? "unavailable",
                new("Native label", label?.Text ?? "Unavailable", $"CharacterProfile text node #{pair.LabelId}"),
                new("Raw label bytes", label?.RawHex ?? "unavailable", $"CharacterProfile text node #{pair.LabelId} UTF-8/SeString"),
                new("Raw value bytes", value?.RawHex ?? "unavailable", $"CharacterProfile text node #{pair.ValueId} UTF-8/SeString"));
            if (reason.Length != 0) issues.Add($"{pair.Label} unavailable: {reason}.");
        }
        return true;
    }

    private sealed record TextStamp(nint Cell, nint Address, uint Id, nint Parent, NodeFlags Flags,
        nint TextAddress, long Used, long Capacity, long Length, string Text, string RawHex, string Issue,
        IReadOnlyList<ParentStamp> Parents);
    private readonly record struct ParentStamp(nint Address, uint Id, nint Parent, NodeFlags Flags);
    private readonly record struct NodeIdentity(nint Cell, nint Address, uint Id, NodeType Type);

    private static bool VerifyIdentity(NodeIdentity before)
        => NativeSheetReader.TryCopy(before.Cell, out nint address) && address == before.Address
            && (address == 0 || NativeSheetReader.TryCopy(address, out AtkResNode node) && node.NodeId == before.Id && node.Type == before.Type);

    private static TextStamp ReadNode(nint cell, nint address, uint id)
    {
        var parents = new List<ParentStamp>();
        if (!NativeSheetReader.TryCopy(address, out AtkTextNode node) || node.NodeId != id || node.Type != NodeType.Text)
            return new(cell, address, id, 0, 0, 0, 0, 0, 0, "Unavailable", "", "node is unreadable or not text", parents);
        var textAddress = (nint)node.NodeText.StringPtr.Value;
        var issue = (node.NodeFlags & NodeFlags.Visible) == 0 ? "node is hidden" : string.Empty;
        var parent = (nint)node.ParentNode;
        var seen = new HashSet<nint> { address };
        while (parent != 0 && issue.Length == 0)
        {
            if (parents.Count >= 32 || !seen.Add(parent) || !NativeSheetReader.TryCopy(parent, out AtkResNode ancestor))
            { issue = "parent chain is unreadable or cyclic"; break; }
            parents.Add(new(parent, ancestor.NodeId, (nint)ancestor.ParentNode, ancestor.NodeFlags));
            if ((ancestor.NodeFlags & NodeFlags.Visible) == 0) { issue = "parent node is hidden"; break; }
            parent = (nint)ancestor.ParentNode;
        }
        var text = "Unavailable";
        var raw = string.Empty;
        if (issue.Length == 0)
        {
            if (textAddress == 0 || node.NodeText.BufUsed < 1 || node.NodeText.BufUsed > UiMenuReader.MaxStringBytes
                || node.NodeText.BufSize < node.NodeText.BufUsed || node.NodeText.StringLength < 0
                || node.NodeText.StringLength > node.NodeText.BufUsed - 1
                || node.NodeText.IsUsingInlineBuffer && (node.NodeText.BufUsed > 64 || node.NodeText.BufSize > 64))
                issue = "text length, capacity or pointer is invalid";
            else
            {
                text = UiMenuReader.ReadText(textAddress, out issue, out raw, plainLabel: true);
                if (issue.Length == 0 && raw.Length / 2 != node.NodeText.BufUsed - 1) issue = "text length and terminator disagree";
                if (issue.Length == 0 && string.IsNullOrWhiteSpace(text)) issue = "text is empty";
            }
        }
        return new(cell, address, id, (nint)node.ParentNode, node.NodeFlags, textAddress, node.NodeText.BufUsed,
            node.NodeText.BufSize, node.NodeText.StringLength, text, raw, issue, parents);
    }

    private static bool VerifyNode(TextStamp before)
    {
        if (!NativeSheetReader.TryCopy(before.Cell, out nint address) || address != before.Address) return false;
        var after = ReadNode(before.Cell, address, before.Id);
        return before.Id == after.Id && before.Parent == after.Parent && before.Flags == after.Flags
            && before.TextAddress == after.TextAddress && before.Used == after.Used && before.Capacity == after.Capacity
            && before.Length == after.Length && before.Text == after.Text && before.RawHex == after.RawHex
            && before.Issue == after.Issue && before.Parents.SequenceEqual(after.Parents);
    }

    private static bool TryAddon(nint address, out AtkUnitBase addon)
        => NativeSheetReader.TryCopy(address, out addon) && addon.IsVisible && addon.IsReady;
    private static bool TryPlayer(nint address, out PlayerState player)
        => NativeSheetReader.TryCopy(address, out player) && player.IsLoaded && player.ContentId != 0;
    private static bool TryCharacter(nint address, PlayerState player, out Character character)
        => NativeSheetReader.TryCopy(address, out character) && character.ObjectKind == ObjectKind.Pc
            && character.ContentId == player.ContentId && player.EntityId != 0 && character.EntityId == player.EntityId;
    private static bool TryItemLevel(nint address, PlayerState player, out ushort itemLevel)
    {
        itemLevel = 0;
        return TryOffset(address, (int)Marshal.OffsetOf<UIState>(nameof(UIState.PlayerState)), out var playerAddress)
            && TryPlayer(playerAddress, out var owner) && owner.ContentId == player.ContentId && owner.EntityId == player.EntityId
            && TryOffset(address, (int)Marshal.OffsetOf<UIState>(nameof(UIState.CurrentItemLevel)), out var levelAddress)
            && NativeSheetReader.TryCopy(levelAddress, out itemLevel);
    }

    private static bool SamePlayer(PlayerState before, PlayerState after)
        => before.ContentId == after.ContentId && before.EntityId == after.EntityId
            && before.CurrentClassJobId == after.CurrentClassJobId && before.CurrentLevel == after.CurrentLevel
            && before.Sex == after.Sex && before.Race == after.Race && before.Tribe == after.Tribe
            && before.GrandCompany == after.GrandCompany && before.GCRanks.SequenceEqual(after.GCRanks)
            && before.GuardianDeity == after.GuardianDeity && before.BirthMonth == after.BirthMonth
            && before.BirthDay == after.BirthDay && before.StartTown == after.StartTown
            && before.BaseStrength == after.BaseStrength && before.BaseDexterity == after.BaseDexterity
            && before.BaseVitality == after.BaseVitality && before.BaseIntelligence == after.BaseIntelligence
            && before.BaseMind == after.BaseMind && before.BasePiety == after.BasePiety
            && before.Attributes.SequenceEqual(after.Attributes);

    private static bool SameCharacter(Character before, Character after)
        => before.ContentId == after.ContentId && before.EntityId == after.EntityId && before.ObjectKind == after.ObjectKind
            && before.Health == after.Health && before.MaxHealth == after.MaxHealth && before.Mana == after.Mana
            && before.MaxMana == after.MaxMana && before.GatheringPoints == after.GatheringPoints
            && before.MaxGatheringPoints == after.MaxGatheringPoints && before.CraftingPoints == after.CraftingPoints
            && before.MaxCraftingPoints == after.MaxCraftingPoints && before.FreeCompanyTag.SequenceEqual(after.FreeCompanyTag);

    private static string? ReadTag(Character character)
    {
        var bytes = character.FreeCompanyTag;
        var length = bytes.IndexOf((byte)0);
        if (length < 0) return null;
        try { return StrictUtf8.GetString(bytes[..length]); }
        catch (DecoderFallbackException) { return null; }
    }

    private static bool TryOffset(nint address, int offset, out nint result)
    {
        result = 0;
        if (address == 0 || offset < 0 || (nuint)address > nuint.MaxValue - (nuint)offset) return false;
        result = (nint)((nuint)address + (nuint)offset);
        return true;
    }

    private static void Add(List<UiMenuEntry> entries, string label, string value, string group, string source, string raw,
        params UiMenuField[] additional)
        => entries.Add(new(entries.Count, label, value)
        {
            Fields = new[] { new UiMenuField("Value", value, source), new("Group", group, "HUD Navigator grouping"),
                new("Source", source, "Field provenance"), new("Raw", raw, source) }.Concat(additional).ToArray(),
        });
    private static string Number<T>(T value) where T : IConvertible => value.ToString(CultureInfo.InvariantCulture);
    private static UiMenuSnapshot Unavailable(string name, AtkUnitBase* addon, string issue)
        => new(name, (ulong)(nuint)addon, "Character", "Character details — unavailable", Array.Empty<UiMenuEntry>())
        { CapturedAtUtc = DateTime.UtcNow, IsDecoded = false, IsPartial = true, IsVisible = false, Status = issue };

    private static string? Lookup(string field, uint id) => field switch
    {
        "GrandCompany.Name" => Plugin.DataManager.GetExcelSheet<GrandCompany>()?.GetRowOrDefault(id)?.Name.ToString(),
        "GCRankLimsaMaleText.Singular" => Plugin.DataManager.GetExcelSheet<GCRankLimsaMaleText>()?.GetRowOrDefault(id)?.Singular.ToString(),
        "GCRankLimsaFemaleText.Singular" => Plugin.DataManager.GetExcelSheet<GCRankLimsaFemaleText>()?.GetRowOrDefault(id)?.Singular.ToString(),
        "GCRankGridaniaMaleText.Singular" => Plugin.DataManager.GetExcelSheet<GCRankGridaniaMaleText>()?.GetRowOrDefault(id)?.Singular.ToString(),
        "GCRankGridaniaFemaleText.Singular" => Plugin.DataManager.GetExcelSheet<GCRankGridaniaFemaleText>()?.GetRowOrDefault(id)?.Singular.ToString(),
        "GCRankUldahMaleText.Singular" => Plugin.DataManager.GetExcelSheet<GCRankUldahMaleText>()?.GetRowOrDefault(id)?.Singular.ToString(),
        "GCRankUldahFemaleText.Singular" => Plugin.DataManager.GetExcelSheet<GCRankUldahFemaleText>()?.GetRowOrDefault(id)?.Singular.ToString(),
        "Race.Masculine" => Plugin.DataManager.GetExcelSheet<Race>()?.GetRowOrDefault(id)?.Masculine.ToString(),
        "Race.Feminine" => Plugin.DataManager.GetExcelSheet<Race>()?.GetRowOrDefault(id)?.Feminine.ToString(),
        "Tribe.Masculine" => Plugin.DataManager.GetExcelSheet<Tribe>()?.GetRowOrDefault(id)?.Masculine.ToString(),
        "Tribe.Feminine" => Plugin.DataManager.GetExcelSheet<Tribe>()?.GetRowOrDefault(id)?.Feminine.ToString(),
        _ => null,
    };
}
