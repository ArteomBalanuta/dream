using System.Text;
using System.Text.Json;
using Xunit;
using Dream.Multiplayer.Protocol;
using Dream.Simulation.Content;
using Dream.Simulation.Inventory;
using Dream.Simulation.Stats;
using Dream.Tests.Support;

namespace Dream.Tests.Multiplayer.Protocol;

// Protocol v13: the compact inventory snapshot. Bytes pinned for every shape of entry, a round trip of what
// the service builds, the rehydration of item text from the shipped catalog, and a reader that survives a
// hostile server: entry and modifier caps, entries it cannot place, wrong types, huge numbers, deep nesting.
public sealed class PlayerInventorySnapshotConverterTests
{
    private static readonly JsonSerializerOptions Options = new() { Converters = { new PlayerInventorySnapshotJsonConverter() } };

    private static string Write(PlayerInventorySnapshot snapshot) => Encoding.UTF8.GetString(JsonSerializer.SerializeToUtf8Bytes(snapshot, Options));

    private static PlayerInventorySnapshot Read(string json) => JsonSerializer.Deserialize<PlayerInventorySnapshot>(json, Options);

    private static string Envelope(string bag, string worn = "[]") => $"{{\"playerName\":\"p\",\"columns\":8,\"rows\":8,\"bag\":{bag},\"worn\":{worn}}}";

    private static string Entries(int count, string entry = "[0,1,\"tool.torch\",0,[]]") => "[" + string.Join(",", Enumerable.Repeat(entry, count)) + "]";

    // Every field of an item, in one line, for comparing what went in with what came out.
    private static string Describe(InventoryItemSnapshot item) =>
        $"{item.ItemInstanceId}|{item.ItemKind}|{item.DisplayName}|{item.VisualId}|{item.StackCount}|{item.Description}|" +
        $"{string.Join("/", item.DetailLines)}|{item.HeldLight}|{item.EquipSlots}|{item.Rarity}|{item.IsRolled}|" +
        string.Join(",", (item.Modifiers ?? []).Select(m => $"{m.Stat}.{m.Op}.{BitConverter.SingleToInt32Bits(m.Value)}")) + (item.Modifiers == null ? "|null" : string.Empty);

    private static string Describe(PlayerInventorySnapshot snapshot) =>
        string.Join(
            "\n",
            new[] { $"{snapshot.PlayerName}|{snapshot.Columns}|{snapshot.Rows}" }
                .Concat(snapshot.Slots.Select(slot => $"bag {slot.SlotIndex} {slot.IsEquipped}: " + (slot.Item is { } item ? Describe(item) : "-")))
                .Append("main " + (snapshot.EquippedSlot is { } main ? $"{main.SlotIndex} {main.IsEquipped}: " + (main.Item is { } m ? Describe(m) : "-") : "none"))
                .Concat((snapshot.Equipment ?? []).Select(slot => $"worn {slot.SlotIndex} {slot.IsEquipped}: " + (slot.Item is { } item ? Describe(item) : "-"))));

    // ---- bytes ----

    private static PlayerInventorySnapshot Sample()
    {
        InventoryItemSnapshot renamed = InventoryWireSamples.Item("gear.ward-pendant", 12) with { DisplayName = "Old Name" };
        InventoryItemSnapshot inert = InventoryWireSamples.Item("gear.ward-lantern", 14, ItemRarity.Rare, rolled: true, [new StatModifier(StatId.Armor, StatOp.Flat, 5f)])
            with { HeldLight = HeldLightKind.None, Modifiers = null };
        return new PlayerInventorySnapshot(
            "Pat",
            8,
            8,
            [
                new InventorySlotSnapshot(0, InventoryWireSamples.Item("gear.leather-cap", 10, ItemRarity.Rare, rolled: true, [new StatModifier(StatId.Armor, StatOp.Flat, 5f), new StatModifier(StatId.Vitality, StatOp.Flat, 2f)])),
                new InventorySlotSnapshot(1, InventoryWireSamples.Item("gear.iron-band", 11, stackCount: 3)),
                new InventorySlotSnapshot(2, renamed),
                new InventorySlotSnapshot(3, InventoryWireSamples.Unknown("tool.gone", 13, "Gone", "held.gone", stackCount: 2)),
                new InventorySlotSnapshot(4, InventoryWireSamples.Item("gear.iron-band", 15, ItemRarity.Common, rolled: true, [new StatModifier(StatId.Strength, StatOp.Percent, 10f)])),
                new InventorySlotSnapshot(5, InventoryWireSamples.Item("gear.iron-blade", 17, ItemRarity.Uncommon, rolled: true, [new StatModifier(StatId.AttackSpeed, StatOp.Flat, 0.25f)])),
                new InventorySlotSnapshot(200, InventoryWireSamples.Item("gear.iron-blade", 16)),
            ],
            new InventorySlotSnapshot(100, InventoryWireSamples.Item("tool.torch", 5), true),
            [new InventorySlotSnapshot(102, inert, true)]);
    }

    [Fact]
    public void EveryShapeOfEntry_IsWrittenAsPinned()
    {
        string[] expected =
        [
            "{\"playerName\":\"Pat\",\"columns\":8,\"rows\":8,\"bag\":[",
            "[0,10,\"gear.leather-cap\",6,[6,5,2,2]],",
            "[1,11,\"gear.iron-band\",0,[],3],",
            "[2,12,\"gear.ward-pendant\",0,[],1,\"Old Name\"],",
            "[3,13,\"tool.gone\",8,[],2,\"Gone\",\"held.gone\"],",
            "[4,15,\"gear.iron-band\",4,[256,10]],",
            "[5,17,\"gear.iron-blade\",5,[4,0.25]],",
            "[200,16,\"gear.iron-blade\",0,[]]],",
            "\"worn\":[[100,5,\"tool.torch\",0,[]],[102,14,\"gear.ward-lantern\",14,[]]]}",
        ];
        Assert.Equal(string.Concat(expected), Write(Sample()));
    }

    [Fact]
    public void WhatTheServerBuilds_RoundTripsFieldForField()
    {
        PlayerInventorySnapshot sample = Sample();

        PlayerInventorySnapshot back = Read(Write(sample));

        Assert.Equal(Describe(sample), Describe(back));
    }

    [Fact]
    public void TheStarterKitFromTheService_RoundTripsFieldForField()
    {
        PlayerInventorySnapshot starter = InventoryWireSamples.Starter("Alice");

        Assert.Equal(Describe(starter), Describe(Read(Write(starter))));
    }

    [Fact]
    public void AnEmptyInventory_IsFourPropertiesAndAnEmptyMainHand()
    {
        string json = Write(PlayerInventorySnapshot.Empty("Zed"));

        Assert.Equal("""{"playerName":"Zed","columns":8,"rows":8,"bag":[],"worn":[]}""", json);
        PlayerInventorySnapshot back = Read(json);
        Assert.Equal("Zed", back.PlayerName);
        Assert.Empty(back.Slots);
        Assert.Equal(new InventorySlotSnapshot(100, null), back.EquippedSlot);
        Assert.Null(back.Equipment);
    }

    [Fact]
    public void TheWriter_LeavesOutEmptyCellsAndAtMostEightModifiers()
    {
        StatModifier[] many = [.. Enumerable.Range(0, 12).Select(i => new StatModifier(StatId.Strength, StatOp.Flat, i))];
        PlayerInventorySnapshot snapshot = new(
            "p",
            8,
            8,
            [new InventorySlotSnapshot(3, null), new InventorySlotSnapshot(4, InventoryWireSamples.Item("gear.iron-band", 1, ItemRarity.Rare, rolled: true, many))]);

        string json = Write(snapshot);

        Assert.DoesNotContain("[3,", json, StringComparison.Ordinal);
        Assert.Contains("[0,0,0,1,0,2,0,3,0,4,0,5,0,6,0,7]", json, StringComparison.Ordinal);
        Assert.DoesNotContain(",8]", json, StringComparison.Ordinal);
        Assert.Single(Read(json).Slots);
    }

    // ---- rehydration ----

    [Fact]
    public void AKnownKind_TakesItsTextLightSlotsAndModifiersFromTheCatalog()
    {
        PlayerInventorySnapshot back = Read(Envelope("""[[7,5,"tool.torch",0,[]],[8,6,"tool.builder-hammer",0,[]]]"""));

        InventoryItemSnapshot torch = back.Slots[0].Item!.Value;
        ItemDefinition definition = ContentCatalog.Shipped.Items.Single(item => item.Key == "tool.torch");
        Assert.Equal(definition.DisplayName, torch.DisplayName);
        Assert.Equal(definition.VisualId, torch.VisualId);
        Assert.Equal(definition.Description, torch.Description);
        Assert.Equal(definition.DetailLines, torch.DetailLines);
        Assert.Equal(HeldLightKind.Torch, torch.HeldLight);
        Assert.Equal(definition.EquipSlotMask, torch.EquipSlots);
        Assert.Null(torch.Modifiers);
        Assert.False(torch.IsRolled);
        // An unrolled item takes the definition's modifiers.
        InventoryItemSnapshot hammer = back.Slots[1].Item!.Value;
        Assert.Equal(ContentCatalog.Shipped.Items.Single(item => item.Key == "tool.builder-hammer").Modifiers, hammer.Modifiers);
    }

    [Fact]
    public void AKindTheCatalogDoesNotKnow_UsesItsWireNameAndVisual_OrTheKindWhenTheyAreMissing()
    {
        PlayerInventorySnapshot back = Read(Envelope("""[[0,1,"tool.gone",8,[],2,"Gone","held.gone"],[1,2,"tool.other",8,[]]]"""));

        InventoryItemSnapshot named = back.Slots[0].Item!.Value;
        Assert.Equal("Gone", named.DisplayName);
        Assert.Equal("held.gone", named.VisualId);
        Assert.Equal(2, named.StackCount);
        Assert.Equal(ToolCatalog.UnknownItemDescription, named.Description);
        Assert.Empty(named.DetailLines);
        Assert.Null(named.Modifiers);
        Assert.Equal(HeldLightKind.None, named.HeldLight);
        Assert.Equal(0, named.EquipSlots);
        InventoryItemSnapshot bare = back.Slots[1].Item!.Value;
        Assert.Equal("tool.other", bare.DisplayName);
        Assert.Equal(string.Empty, bare.VisualId);
    }

    [Fact]
    public void ARolledItemTakesItsModifiersFromTheWire_AndAnInertOneTakesNone()
    {
        PlayerInventorySnapshot back = Read(Envelope(
            """[[0,1,"gear.leather-cap",6,[6,5,2,2]],[1,2,"gear.ward-lantern",14,[6,4]],[2,3,"gear.leather-cap",6,[]]]"""));

        InventoryItemSnapshot rolled = back.Slots[0].Item!.Value;
        Assert.True(rolled.IsRolled);
        Assert.Equal(ItemRarity.Rare, rolled.Rarity);
        Assert.Equal([new StatModifier(StatId.Armor, StatOp.Flat, 5f), new StatModifier(StatId.Vitality, StatOp.Flat, 2f)], rolled.Modifiers);
        // Inert: no light and no stats, whatever the wire carries.
        InventoryItemSnapshot inert = back.Slots[1].Item!.Value;
        Assert.Null(inert.Modifiers);
        Assert.Equal(HeldLightKind.None, inert.HeldLight);
        // A rolled item with no modifiers (a damaged roll) has none: it does not fall back to the definition's.
        Assert.Null(back.Slots[2].Item!.Value.Modifiers);
    }

    [Fact]
    public void ANonFlatModifier_KeepsItsOp_AndAnUnknownStatOrOpIsDropped()
    {
        PlayerInventorySnapshot back = Read(Envelope("""[[0,1,"gear.iron-band",4,[256,10,3,1,99,1,512,1,-1,1]]]"""));

        Assert.Equal([new StatModifier(StatId.Strength, StatOp.Percent, 10f), new StatModifier(StatId.Energy, StatOp.Flat, 1f)], back.Slots[0].Item!.Value.Modifiers);
    }

    // ---- placing entries ----

    [Fact]
    public void TheMainHandIsTheSlot100Entry_AndTheOtherWornEntriesAreSortedByWornSlot()
    {
        PlayerInventorySnapshot back = Read(Envelope("[]", """[[105,3,"tool.torch",0,[]],[100,1,"tool.torch",0,[]],[101,2,"tool.torch",0,[]]]"""));

        Assert.Equal(1, back.EquippedSlot!.Value.Item!.Value.ItemInstanceId);
        Assert.True(back.EquippedSlot.Value.IsEquipped);
        Assert.Equal([101, 105], back.Equipment!.Select(slot => slot.SlotIndex));
        Assert.All(back.Equipment!, slot => Assert.True(slot.IsEquipped));
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("64")]
    [InlineData("99")]
    [InlineData("100")]
    [InlineData("111")]
    [InlineData("199")]
    [InlineData("216")]
    [InlineData("2147483647")]
    public void ABagEntryOutsideTheBagAndTheHeldBackRows_IsSkipped(string slot)
    {
        PlayerInventorySnapshot back = Read(Envelope($"[[{slot},1,\"tool.torch\",0,[]],[2,2,\"tool.torch\",0,[]]]"));

        Assert.Equal(2, Assert.Single(back.Slots).SlotIndex);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("0")]
    [InlineData("63")]
    [InlineData("99")]
    [InlineData("112")]
    [InlineData("200")]
    [InlineData("2147483647")]
    public void AWornEntryOutsideTheTwelveWornSlots_IsSkipped(string slot)
    {
        PlayerInventorySnapshot back = Read(Envelope("[]", $"[[{slot},1,\"tool.torch\",0,[]]]"));

        Assert.Null(back.EquippedSlot!.Value.Item);
        Assert.Null(back.Equipment);
    }

    [Fact]
    public void TwoEntriesOnOneAnchor_AreBothKept_TheClientDecidesWhichWins()
    {
        PlayerInventorySnapshot back = Read(Envelope("""[[5,1,"tool.torch",0,[]],[5,2,"tool.torch",0,[]]]"""));

        Assert.Equal(2, back.Slots.Count);
    }

    // ---- hostile input ----

    [Fact]
    public void TwoHundredEntries_AreRead_AndTwoHundredAndOneAreRefused()
    {
        Assert.Equal(200, Read(Envelope(Entries(200))).Slots.Count);

        Assert.Throws<JsonException>(() => Read(Envelope(Entries(201))));
    }

    [Fact]
    public void TheCapCountsTheBagAndTheWornListTogether()
    {
        Assert.Equal(100, Read(Envelope(Entries(100), Entries(100, "[101,1,\"tool.torch\",0,[]]"))).Slots.Count);

        Assert.Throws<JsonException>(() => Read(Envelope(Entries(100), Entries(101, "[101,1,\"tool.torch\",0,[]]"))));
    }

    [Fact]
    public void EightModifiers_AreRead_AndNineAreRefused()
    {
        string eight = string.Join(",", Enumerable.Repeat("0,1", 8));
        string nine = string.Join(",", Enumerable.Repeat("0,1", 9));

        Assert.Equal(8, Read(Envelope($"[[0,1,\"gear.iron-band\",4,[{eight}]]]")).Slots[0].Item!.Value.Modifiers!.Count);

        Assert.Throws<JsonException>(() => Read(Envelope($"[[0,1,\"gear.iron-band\",4,[{nine}]]]")));
    }

    [Theory]
    [InlineData("""{"playerName":"p","columns":8,"rows":8,"bag":{},"worn":[]}""")]
    [InlineData("""{"playerName":"p","columns":8,"rows":8,"bag":"x","worn":[]}""")]
    [InlineData("""{"playerName":"p","columns":8,"rows":8,"bag":[7],"worn":[]}""")]
    [InlineData("""{"playerName":"p","columns":8,"rows":8,"bag":[null],"worn":[]}""")]
    [InlineData("""{"playerName":"p","columns":8,"rows":8,"bag":[{}],"worn":[]}""")]
    [InlineData("""{"playerName":"p","columns":8,"rows":8,"bag":[[]],"worn":[]}""")]
    [InlineData("""{"playerName":"p","columns":8,"rows":8,"bag":[[0]],"worn":[]}""")]
    [InlineData("""{"playerName":"p","columns":8,"rows":8,"bag":[[0,1]],"worn":[]}""")]
    [InlineData("""{"playerName":"p","columns":8,"rows":8,"bag":[[0,1,"tool.torch"]],"worn":[]}""")]
    [InlineData("""{"playerName":"p","columns":8,"rows":8,"bag":[[0,1,"tool.torch",0]],"worn":[]}""")]
    [InlineData("""{"playerName":"p","columns":8,"rows":8,"bag":[[0,1,"tool.torch",0,7]],"worn":[]}""")]
    [InlineData("""{"playerName":"p","columns":8,"rows":8,"bag":[["0",1,"tool.torch",0,[]]],"worn":[]}""")]
    [InlineData("""{"playerName":"p","columns":8,"rows":8,"bag":[[0.5,1,"tool.torch",0,[]]],"worn":[]}""")]
    [InlineData("""{"playerName":"p","columns":8,"rows":8,"bag":[[0,"1","tool.torch",0,[]]],"worn":[]}""")]
    [InlineData("""{"playerName":"p","columns":8,"rows":8,"bag":[[0,1.5,"tool.torch",0,[]]],"worn":[]}""")]
    [InlineData("""{"playerName":"p","columns":8,"rows":8,"bag":[[0,1,5,0,[]]],"worn":[]}""")]
    [InlineData("""{"playerName":"p","columns":8,"rows":8,"bag":[[0,1,null,0,[]]],"worn":[]}""")]
    [InlineData("""{"playerName":"p","columns":8,"rows":8,"bag":[[0,1,"tool.torch","x",[]]],"worn":[]}""")]
    [InlineData("""{"playerName":"p","columns":8,"rows":8,"bag":[[0,1,"tool.torch",0,[0]]],"worn":[]}""")]
    [InlineData("""{"playerName":"p","columns":8,"rows":8,"bag":[[0,1,"tool.torch",0,["a",1]]],"worn":[]}""")]
    [InlineData("""{"playerName":"p","columns":8,"rows":8,"bag":[[0,1,"tool.torch",0,[0,"1"]]],"worn":[]}""")]
    [InlineData("""{"playerName":"p","columns":8,"rows":8,"bag":[[0,1,"tool.torch",0,[0,[1]]]],"worn":[]}""")]
    [InlineData("""{"playerName":"p","columns":8,"rows":8,"bag":[[0,1,"tool.torch",0,[],"x"]],"worn":[]}""")]
    [InlineData("""{"playerName":"p","columns":8,"rows":8,"bag":[[0,1,"tool.torch",0,[],1,5]],"worn":[]}""")]
    [InlineData("""{"playerName":"p","columns":"8","rows":8,"bag":[],"worn":[]}""")]
    [InlineData("""{"playerName":5,"columns":8,"rows":8,"bag":[],"worn":[]}""")]
    [InlineData("""[]""")]
    [InlineData("""7""")]
    [InlineData("""{"playerName":"p","columns":8,"rows":8,"bag":[[0,1,"tool.torch",0,[]]""")]
    public void AMalformedSnapshot_IsAJsonException_NeverAnythingElse(string json)
    {
        Assert.Throws<JsonException>(() => Read(json));
    }

    [Theory]
    [InlineData("99999999999")]
    [InlineData("-99999999999")]
    [InlineData("1e400")]
    [InlineData("1e9999999999")]
    public void ANumberTooBigForTheField_IsAJsonException(string number)
    {
        Assert.Throws<JsonException>(() => Read(Envelope($"[[{number},1,\"tool.torch\",0,[]]]")));
        Assert.Throws<JsonException>(() => Read(Envelope($"[[0,{number}0000000000,\"tool.torch\",0,[]]]")));
        Assert.Throws<JsonException>(() => Read(Envelope($"[[0,1,\"tool.torch\",{number},[]]]")));
        Assert.Throws<JsonException>(() => Read(Envelope($"[[0,1,\"tool.torch\",0,[],{number}]]")));
    }

    [Theory]
    [InlineData("1e400")]
    [InlineData("-1e400")]
    [InlineData("3.5e38")]
    [InlineData("1e39")]
    [InlineData("1e-400")]
    public void AModifierValueOutOfRange_IsAJsonException_OrAFiniteNumber(string value)
    {
        Exception? error = Record.Exception(() =>
        {
            PlayerInventorySnapshot back = Read(Envelope($"[[0,1,\"gear.iron-band\",4,[0,{value}]]]"));
            Assert.All(back.Slots[0].Item!.Value.Modifiers ?? [], modifier => Assert.True(float.IsFinite(modifier.Value)));
        });

        Assert.True(error is null or JsonException, error?.ToString());
    }

    [Fact]
    public void AHugeIdFlagsAndStack_AreReadAsNumbers_NotRefused()
    {
        PlayerInventorySnapshot back = Read(Envelope($"[[0,{long.MaxValue},\"tool.torch\",{int.MaxValue},[],{int.MinValue}]]"));

        InventoryItemSnapshot item = back.Slots[0].Item!.Value;
        Assert.Equal(long.MaxValue, item.ItemInstanceId);
        Assert.Equal(int.MinValue, item.StackCount);
        // Every flag bit set: the rarity bits 3 are not a rarity, so Common; rolled; inert.
        Assert.Equal(ItemRarity.Common, item.Rarity);
        Assert.True(item.IsRolled);
        Assert.Equal(HeldLightKind.None, item.HeldLight);
    }

    [Fact]
    public void ATextLongerThan256Characters_IsAJsonException()
    {
        string longText = new('x', PlayerInventorySnapshotJsonConverter.MaxTextLength + 1);
        string ok = new('x', PlayerInventorySnapshotJsonConverter.MaxTextLength);

        Assert.Equal(ok, Read(Envelope($"[[0,1,\"tool.gone\",8,[],1,\"{ok}\",\"v\"]]")).Slots[0].Item!.Value.DisplayName);
        Assert.Throws<JsonException>(() => Read(Envelope($"[[0,1,\"{longText}\",0,[]]]")));
        Assert.Throws<JsonException>(() => Read(Envelope($"[[0,1,\"tool.gone\",8,[],1,\"{longText}\",\"v\"]]")));
        Assert.Throws<JsonException>(() => Read(Envelope($"[[0,1,\"tool.gone\",8,[],1,\"n\",\"{longText}\"]]")));
    }

    [Fact]
    public void ADeeplyNestedEntry_IsAJsonException()
    {
        string nested = string.Concat(Enumerable.Repeat("[", 200)) + string.Concat(Enumerable.Repeat("]", 200));

        Assert.Throws<JsonException>(() => Read(Envelope($"[{nested}]")));
        Assert.Throws<JsonException>(() => Read(Envelope($"[[0,1,\"tool.torch\",0,[],1,\"n\",\"v\",{nested}]]")));
    }

    [Fact]
    public void ExtraTrailingElements_UnknownPropertiesAnyOrderAndNullLists_AreAccepted()
    {
        PlayerInventorySnapshot back = Read(
            """{"worn":null,"extra":{"a":[1,[2,{"b":3}]]},"bag":[[1,7,"tool.torch",0,[],1,"n","v",{"more":[1]},9]],"rows":8,"columns":8,"playerName":"x"}""");

        Assert.Equal("x", back.PlayerName);
        Assert.Equal(7, Assert.Single(back.Slots).Item!.Value.ItemInstanceId);
        Assert.Null(back.Equipment);
    }

    [Fact]
    public void AMissingProperty_LeavesItsDefault()
    {
        PlayerInventorySnapshot back = Read("{}");

        Assert.Equal(string.Empty, back.PlayerName);
        Assert.Equal(0, back.Columns);
        Assert.Empty(back.Slots);
        Assert.Null(back.EquippedSlot!.Value.Item);
    }

    // A v12 inventory has no "bag": nothing in it is read as an item, and it is not an error to the reader.
    [Fact]
    public void AV12ShapedInventory_ReadsAsAnEmptyBag()
    {
        PlayerInventorySnapshot back = Read(
            """{"playerName":"p","columns":4,"rows":4,"slots":[{"slotIndex":0,"item":{"itemInstanceId":1,"itemKind":"tool.torch"}}],"equippedSlot":{"slotIndex":100,"isEquipped":true}}""");

        Assert.Empty(back.Slots);
    }

    // ---- through the codec ----

    [Fact]
    public void AV12Client_IsRefusedByTheVersion_BeforeAnyInventoryIsRead()
    {
        string json = """{"protocolVersion":12,"messageKind":"server.action","sequence":1,"payload":{"result":{"sessionId":"s","actionSequence":1,"serverTick":1,"accepted":true,"payload":{"$type":"inventory.changed","inventory":{"slots":[]}}}}}""";

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => new TransportJsonCodec().Decode(Encoding.UTF8.GetBytes(json)));

        Assert.Equal("Unsupported protocol version 12. Supported version: 16.", error.Message);
    }

    [Fact]
    public void TheCodec_UsesTheCompactShape_ForTheJoinThePushAndTheActionResult()
    {
        PlayerInventorySnapshot starter = InventoryWireSamples.Starter("Alice");
        TransportJsonCodec codec = new();
        string Frame<T>(T message, string kind)
            where T : IProtocolMessage =>
            Encoding.UTF8.GetString(codec.Encode(new TransportEnvelope(TransportEnvelope.CurrentProtocolVersion, kind, 1), message));

        string join = Frame(new JoinAcceptedMessage("s", 1, 2, null, Inventory: starter), "server.join-accepted");
        string push = Frame(new InventoryPushMessage(starter, []), InventoryPushMessage.Kind);
        string result = Frame(
            new ServerActionMessage(new AuthoritativeActionResult("s", 1, 1, true, new InventoryChangedResult(starter))),
            "server.action");

        foreach (string frame in new[] { join, push, result })
        {
            Assert.Contains("\"bag\":[[2,2,\"tool.field-blade\",0,[]]", frame, StringComparison.Ordinal);
            Assert.Contains("\"worn\":[[100,1,\"tool.builder-hammer\",0,[]]]", frame, StringComparison.Ordinal);
            Assert.DoesNotContain("\"slots\"", frame, StringComparison.Ordinal);
            Assert.DoesNotContain("detailLines", frame, StringComparison.Ordinal);
        }
    }
}
