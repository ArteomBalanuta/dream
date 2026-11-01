using System.Text;
using OpenTK.Mathematics;
using Xunit;
using Dream.Client.Scenes;
using Dream.Multiplayer.Protocol;
using Dream.Rendering.Hud;
using Dream.Rendering.ViewModels;
using Dream.Simulation.Content;
using Dream.Simulation.Inventory;
using Dream.Simulation.Players;
using Dream.Simulation.Stats;

namespace Dream.Tests.Client.Scenes;

// Wave H3: the client reads equipment from the inventory snapshot alone. PlayScene itself needs a
// GL context, so these run the static pieces it calls (ReadEquipment, ToInventoryIntent).
public sealed class PlaySceneEquipmentSnapshotTests
{
    private static InventoryItemSnapshot SnapshotOf(ItemDefinition definition, long id) => new(
        id,
        definition.Key,
        definition.DisplayName,
        definition.VisualId,
        1,
        definition.Description,
        definition.DetailLines,
        definition.Modifiers.Count == 0 ? null : definition.Modifiers,
        definition.HeldLight,
        definition.EquipSlotMask);

    private static PlayerInventorySnapshot InventoryWith(EquipmentSlot slot, InventoryItemSnapshot item)
    {
        InventorySlotSnapshot entry = new(EquipmentSlots.ToSlotIndex(slot), item, IsEquipped: true);
        return slot == EquipmentSlot.MainHand
            ? new PlayerInventorySnapshot("p", 4, 4, [], entry)
            : new PlayerInventorySnapshot("p", 4, 4, [], null, [entry]);
    }

    private static PlayerCombatant NewPlayer() => new(() => Vector3.Zero);

    private static (PlayerCombatStats Stats, int Armor) ServerSide(ItemDefinition definition, EquipmentSlot slot)
    {
        EquipmentLoadout loadout = new();
        loadout.Set(slot, definition.Modifiers);
        PlayerCombatant player = NewPlayer();
        player.ApplyEquipment(loadout);
        return (player.Stats, player.EffectiveArmor);
    }

    [Fact]
    public void ClientStats_MatchTheServers_ForEveryShippedItem_InEveryAllowedSlot()
    {
        int checkedPairs = 0;
        foreach (ItemDefinition definition in ContentCatalog.Shipped.Items)
        {
            foreach (EquipmentSlot slot in definition.EquipSlots)
            {
                InventoryItemSnapshot item = SnapshotOf(definition, 7);
                EquipmentLoadout loadout = new();
                PlayScene.ReadEquipment(InventoryWith(slot, item), loadout);
                PlayerCombatant client = NewPlayer();
                client.ApplyEquipment(loadout);

                (PlayerCombatStats server, int serverArmor) = ServerSide(definition, slot);
                Assert.Equal(server.Strength, client.Stats.Strength);
                Assert.Equal(BitConverter.SingleToInt32Bits(server.AttackSpeed), BitConverter.SingleToInt32Bits(client.Stats.AttackSpeed));
                Assert.Equal(server.Vitality, client.Stats.Vitality);
                Assert.Equal(serverArmor, client.EffectiveArmor);
                checkedPairs++;
            }
        }

        Assert.True(checkedPairs >= 9);
    }

    [Fact]
    public void RolledModifiers_InTheSnapshot_Win_AndNoCatalogIsConsulted()
    {
        // A kind the shipped catalog has never heard of: only the snapshot's numbers count.
        InventoryItemSnapshot item = new(
            9, "gear.not-in-any-catalog", "Mystery", "held.tool.blade", 1, "d", [],
            [new StatModifier(StatId.Strength, StatOp.Flat, 6f), new StatModifier(StatId.Armor, StatOp.Flat, 4f)],
            HeldLightKind.None, EquipmentSlots.MaskOf(EquipmentSlot.MainHand), ItemRarity.Rare, IsRolled: true);
        EquipmentLoadout loadout = new();

        PlayScene.EquipmentPresentation presentation = PlayScene.ReadEquipment(InventoryWith(EquipmentSlot.MainHand, item), loadout);
        PlayerCombatant player = NewPlayer();
        player.ApplyEquipment(loadout);

        Assert.Equal(16, player.Stats.Strength);
        Assert.Equal(4, player.EffectiveArmor);
        Assert.Equal("held.tool.blade", presentation.MainHandVisualId);
    }

    [Fact]
    public void FourSlots_SumTheirModifiers_AndOffHandLightWins()
    {
        EquipmentLoadout loadout = new();
        PlayerInventorySnapshot inventory = new(
            "p", 4, 4, [],
            new InventorySlotSnapshot(100, Item("blade", "held.tool.blade", HeldLightKind.Torch, new StatModifier(StatId.Strength, StatOp.Flat, 4f))),
            [
                new InventorySlotSnapshot(101, Item("lantern", "held.tool.lantern", HeldLightKind.Lantern, new StatModifier(StatId.Armor, StatOp.Flat, 2f))),
                new InventorySlotSnapshot(102, Item("cap", "worn.head", HeldLightKind.None, new StatModifier(StatId.Armor, StatOp.Flat, 3f))),
                new InventorySlotSnapshot(103, Item("vest", "worn.body", HeldLightKind.None, new StatModifier(StatId.Vitality, StatOp.Flat, 2f)))
            ]);

        PlayScene.EquipmentPresentation presentation = PlayScene.ReadEquipment(inventory, loadout);
        PlayerCombatant player = NewPlayer();
        player.ApplyEquipment(loadout);

        Assert.Equal(14, player.Stats.Strength);
        Assert.Equal(12, player.Stats.Vitality);
        Assert.Equal(5, player.EffectiveArmor);
        Assert.Equal(HeldLightKind.Lantern, presentation.HeldLight);
        Assert.Equal("held.tool.lantern", presentation.OffHandVisualId);
        Assert.Equal("held.tool.blade", presentation.MainHandVisualId);
    }

    [Fact]
    public void MainHandLight_ShowsWhenTheOffHandHasNone()
    {
        PlayerInventorySnapshot inventory = new(
            "p", 4, 4, [],
            new InventorySlotSnapshot(100, Item("torch", "held.tool.torch", HeldLightKind.Torch)));

        PlayScene.EquipmentPresentation presentation = PlayScene.ReadEquipment(inventory, new EquipmentLoadout());

        Assert.Equal(HeldLightKind.Torch, presentation.HeldLight);
        Assert.Null(presentation.OffHandVisualId);
    }

    [Fact]
    public void EmptyMainHand_ShowsNoItem_AndNoSnapshotShowsTheDefault()
    {
        PlayScene.EquipmentPresentation empty = PlayScene.ReadEquipment(PlayerInventorySnapshot.Empty("p"), new EquipmentLoadout());
        PlayScene.EquipmentPresentation none = PlayScene.ReadEquipment(null, new EquipmentLoadout());

        Assert.Equal(ViewModelAssets.EmptyHeldItemId, empty.MainHandVisualId);
        Assert.Equal(HeldLightKind.None, empty.HeldLight);
        Assert.Equal(ViewModelAssets.DefaultLoadout.HeldItemId, none.MainHandVisualId);
    }

    [Fact]
    public void UnknownKindAndVisual_DoNotThrow_ShowTheDebugItem_AndCastNoLight()
    {
        InventoryItemSnapshot item = new(1, "retired.kind", "Old", "visual.nobody-registered", 1, "d", []);
        PlayerInventorySnapshot inventory = new("p", 4, 4, [], new InventorySlotSnapshot(100, item));
        PlayScene.EquipmentPresentation presentation = PlayScene.ReadEquipment(inventory, new EquipmentLoadout());
        ViewModelCatalog catalog = ViewModelCatalog.CreateDefault();
        PlayerViewModelController controller = new(catalog, new Dream.Application.GameSettings(new Vector2i(1280, 720)));

        controller.SetHeldItem(presentation.MainHandVisualId);
        controller.SetOffHandItem(presentation.OffHandVisualId);

        Assert.Equal("held.debug", controller.CurrentFrameState.HeldItem.Definition.Id);
        Assert.Equal(HeldLightKind.None, presentation.HeldLight);
    }

    [Fact]
    public void HostileSnapshot_NeverThrows()
    {
        StatModifier[] forty = Enumerable.Repeat(new StatModifier(StatId.Strength, StatOp.Flat, 1f), 40).ToArray();
        StatModifier[] hostile =
        [
            new((StatId)200, StatOp.Flat, 5f),
            new(StatId.Strength, (StatOp)9, 5f),
            new(StatId.Strength, StatOp.Flat, float.NaN),
            new(StatId.Strength, StatOp.Flat, float.PositiveInfinity),
            new(StatId.Armor, StatOp.Flat, float.MaxValue),
            new(StatId.Armor, StatOp.Flat, float.MaxValue),
            new(StatId.Vitality, StatOp.Flat, float.MinValue)
        ];
        InventoryItemSnapshot Make(StatModifier[] mods, string? visual, HeldLightKind light) =>
            new(1, null!, null!, visual!, 1, null!, null!, mods, light, -1, (ItemRarity)99, true);

        PlayerInventorySnapshot inventory = new(
            "p", int.MaxValue, -4, null!,
            new InventorySlotSnapshot(100, Make(forty, null, (HeldLightKind)77)),
            [
                new InventorySlotSnapshot(101, Make(hostile, "", HeldLightKind.Torch)),
                new InventorySlotSnapshot(100, Make(hostile, "x", HeldLightKind.Torch)),
                new InventorySlotSnapshot(int.MinValue, Make(forty, "y", HeldLightKind.None)),
                new InventorySlotSnapshot(104, null),
                new InventorySlotSnapshot(102, Make(hostile, "held.tool.blade", HeldLightKind.None))
            ]);
        EquipmentLoadout loadout = new();
        PlayerCombatant player = NewPlayer();
        PlayerViewModelController controller = new(
            ViewModelCatalog.CreateDefault(),
            new Dream.Application.GameSettings(new Vector2i(1280, 720)));

        PlayScene.EquipmentPresentation presentation = PlayScene.ReadEquipment(inventory, loadout);
        player.ApplyEquipment(loadout);
        controller.SetHeldItem(presentation.MainHandVisualId);
        controller.SetOffHandItem(presentation.OffHandVisualId);
        _ = CharacterPanelModel.BuildSlotRows(inventory);
        _ = CharacterPanelModel.MoveTargets(inventory, 0);

        Assert.InRange(player.Stats.Strength, PlayerCombatStats.MinStrength, PlayerCombatStats.MaxStrength);
        // The main hand's light value 77 is no light; the off hand's Torch is a real one.
        Assert.Equal(HeldLightKind.Torch, presentation.HeldLight);
        Assert.Equal(8, loadout.Get(EquipmentSlot.MainHand).Length);
        Assert.Equal("held.debug", controller.CurrentFrameState.HeldItem.Definition.Id);
    }

    [Fact]
    public void ReplayingASnapshot_LeavesNoStaleSlot()
    {
        EquipmentLoadout loadout = new();
        PlayScene.ReadEquipment(InventoryWith(EquipmentSlot.Head, Item("cap", "worn.head", HeldLightKind.None, new StatModifier(StatId.Armor, StatOp.Flat, 3f))), loadout);
        Assert.Equal(1, loadout.Get(EquipmentSlot.Head).Length);

        PlayScene.ReadEquipment(PlayerInventorySnapshot.Empty("p"), loadout);

        Assert.Equal(0, loadout.Get(EquipmentSlot.Head).Length);
    }

    [Fact]
    public void AHostileSnapshotWithA1e99Value_DecodedFromRealJson_NeverThrowsAndStaysInRange()
    {
        TransportJsonCodec codec = new();
        InventoryItemSnapshot item = new(
            1, "gear.iron-blade", "Iron Blade", "held.tool.blade", 1, "d", [],
            Modifiers: [new StatModifier(StatId.Strength, StatOp.Flat, 4f), new StatModifier(StatId.Armor, StatOp.Flat, 4f)],
            EquipSlots: 1, Rarity: ItemRarity.Rare, IsRolled: true);
        ServerActionMessage message = new(new AuthoritativeActionResult(
            "s", 1, 1, Accepted: true, Payload: new InventoryChangedResult(new PlayerInventorySnapshot("p", 4, 4, [new InventorySlotSnapshot(0, item)]))));
        string json = Encoding.UTF8.GetString(codec.Encode(
            new TransportEnvelope(TransportEnvelope.CurrentProtocolVersion, message.MessageKind, 1), message));

        // The compact wire entry (protocol v13): [anchor,id,kind,flags,[stat,value,...]].
        const string honest = "[0,1,\"gear.iron-blade\",6,[0,4,6,4]]";
        Assert.Contains(honest, json);
        string hostile = json.Replace(honest, "[0,1,\"gear.iron-blade\",6,[0,1e99,6,4]]");

        PlayerInventorySnapshot decoded;
        try
        {
            decoded = Assert.IsType<InventoryChangedResult>(
                Assert.IsType<ServerActionMessage>(codec.Decode(Encoding.UTF8.GetBytes(hostile)).Message).Result.Payload).Inventory;
        }
        catch (System.Text.Json.JsonException)
        {
            // The decoder refused the value: nothing reaches the loadout at all.
            return;
        }

        EquipmentLoadout loadout = new();
        PlayerInventorySnapshot withEquipment = new(
            decoded.PlayerName, decoded.Columns, decoded.Rows, decoded.Slots,
            new InventorySlotSnapshot(100, decoded.Slots[0].Item));
        PlayScene.ReadEquipment(withEquipment, loadout);
        PlayerCombatant player = NewPlayer();
        player.ApplyEquipment(loadout);

        Assert.InRange(player.Stats.Strength, PlayerCombatStats.MinStrength, PlayerCombatStats.MaxStrength);
        Assert.All(loadout.Get(EquipmentSlot.MainHand).ToArray(), m => Assert.True(float.IsFinite(m.Value)));
    }

    [Fact]
    public void APercentModifierInASnapshot_IsDropped_AndOverflowTimesZeroCannotMakeNaN()
    {
        EquipmentLoadout loadout = new();
        InventoryItemSnapshot hostile = Item(
            "x", "held.debug", HeldLightKind.None,
            new StatModifier(StatId.Strength, StatOp.Flat, float.MaxValue),
            new StatModifier(StatId.Strength, StatOp.Percent, -1f));
        PlayerInventorySnapshot inventory = new(
            "p", 4, 4, [], new InventorySlotSnapshot(100, hostile), []);

        PlayScene.ReadEquipment(inventory, loadout);

        Assert.All(loadout.Get(EquipmentSlot.MainHand).ToArray(), m => Assert.Equal(StatOp.Flat, m.Op));
        PlayerCombatant player = NewPlayer();
        player.ApplyEquipment(loadout);
        Assert.InRange(player.Stats.Strength, PlayerCombatStats.MinStrength, PlayerCombatStats.MaxStrength);
    }

    [Theory]
    [InlineData(InventoryHudActionKind.EquipTo, typeof(InventoryEquipIntent))]
    [InlineData(InventoryHudActionKind.Unequip, typeof(InventoryUnequipIntent))]
    [InlineData(InventoryHudActionKind.Move, typeof(InventoryMoveIntent))]
    [InlineData(InventoryHudActionKind.Destroy, typeof(InventoryDestroyIntent))]
    public void EachHudAction_MapsToExactlyOneMatchingIntent(InventoryHudActionKind kind, Type intentType)
    {
        IPlayerActionPayload intent = PlayScene.ToInventoryIntent(new InventoryHudAction(kind, 3, 101, 42));

        Assert.IsType(intentType, intent);
    }

    [Fact]
    public void IntentFields_CarryTheActionsNumbers()
    {
        Assert.Equal(new InventoryEquipIntent(3, 101), PlayScene.ToInventoryIntent(new(InventoryHudActionKind.EquipTo, 3, 101)));
        // No cell named (-1): the field is left out and the server takes the first fit.
        Assert.Equal(new InventoryUnequipIntent(102), PlayScene.ToInventoryIntent(new(InventoryHudActionKind.Unequip, 102, CharacterPanelModel.FirstFitCell)));
        Assert.Null(((InventoryUnequipIntent)PlayScene.ToInventoryIntent(new(InventoryHudActionKind.Unequip, 102, -1))).TargetCell);
        // A named cell travels, including cell 0, which is the top-left anchor and not "unset".
        Assert.Equal(new InventoryUnequipIntent(102, 37), PlayScene.ToInventoryIntent(new(InventoryHudActionKind.Unequip, 102, 37)));
        Assert.Equal(new InventoryUnequipIntent(102, 0), PlayScene.ToInventoryIntent(new(InventoryHudActionKind.Unequip, 102)));
        Assert.Equal(new InventoryMoveIntent(1, 5), PlayScene.ToInventoryIntent(new(InventoryHudActionKind.Move, 1, 5)));
        Assert.Equal(new InventoryDestroyIntent(2, 99), PlayScene.ToInventoryIntent(new(InventoryHudActionKind.Destroy, 2, 0, 99)));
    }

    private static InventoryItemSnapshot Item(string kind, string visual, HeldLightKind light, params StatModifier[] modifiers) =>
        new(1, kind, kind, visual, 1, "d", [], modifiers.Length == 0 ? null : modifiers, light);
}
