using Dream.Client.Replication;
using Dream.Multiplayer.Protocol;
using Dream.Rendering.Actors;
using Dream.Rendering.Hud;
using Dream.Rendering.Ui;
using Dream.Rendering.Ui.Panels;
using Dream.Simulation.Combat;
using Dream.Simulation.Inventory;
using Dream.Simulation.World;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Rendering.Ui.Panels;

// M3: the ground-item and NPC labels (which are listed, how they read, what a click on one does), the
// placeholder boxes, the wallet line, and the loot lines that go with them.
public sealed class GroundLabelTests : IDisposable
{
    private const string Torch = "tool.torch";

    private readonly InventoryScene _scene = new();

    public void Dispose() => _scene.Dispose();

    private static ClientGroundItemCache CacheWith(params GroundItemSnapshot[] items)
    {
        ClientGroundItemCache cache = new();
        cache.Apply(new ServerGroundItems(true, items, []));
        return cache;
    }

    private static GroundItemSnapshot At(long id, float x, float z, int claimant = 0, ulong expires = 0, ItemRarity rarity = ItemRarity.Common) =>
        new(id, Torch, rarity, new Vector3(x, 0f, z), claimant, expires);

    private static ClientNpcCache NpcAt(int id, float x, float z)
    {
        ClientNpcCache npcs = new();
        npcs.ApplyActors([new ActorSnapshot(id, CreatureKind.Npc, null, new Vector3(x, 0f, z), -Vector3.UnitZ, 1, 1, true, DisplayName: "Mara")]);
        return npcs;
    }

    private static List<WorldEntityLabel> Labels(ClientGroundItemCache ground, ClientNpcCache npcs, int localActor = 1, ulong tick = 10)
    {
        List<WorldEntityLabel> labels = new();
        WorldEntityRenderer.CollectLabels(ground, npcs, Vector3.Zero, localActor, tick, labels);
        return labels;
    }

    [Fact]
    public void OnlyItemsWithinTwelveMetresAreListed_NearestFirst_AtMostEight()
    {
        GroundItemSnapshot[] items = Enumerable.Range(0, 20)
            .Select(n => At(n + 1, 20f - n, 0f))
            .Append(At(100, 11.9f, 0f))
            .ToArray();

        List<WorldEntityLabel> labels = Labels(CacheWith(items), new ClientNpcCache());

        Assert.Equal(WorldEntityRenderer.MaxItemLabels, labels.Count);
        Assert.All(labels, l => Assert.InRange(l.WorldAnchor.X, 0f, WorldEntityRenderer.ItemLabelRange));
        float[] distances = labels.Select(l => l.WorldAnchor.X).ToArray();
        Assert.Equal(distances.OrderBy(d => d), distances);
        Assert.Equal(1f, distances[0]);
    }

    [Fact]
    public void AnItemJustOutOfRangeIsNotListed()
    {
        Assert.Empty(Labels(CacheWith(At(1, 12.5f, 0f)), new ClientNpcCache()));
    }

    [Fact]
    public void AnItemLabelClicksToAPickup_AndSaysEWhenInReach()
    {
        List<WorldEntityLabel> labels = Labels(CacheWith(At(7, 2f, 0f), At(8, 9f, 0f)), new ClientNpcCache());

        Assert.Equal(new HudLabelClick(HudLabelClickKind.PickUpGround, 7), labels[0].Click);
        Assert.Equal(WorldEntityRenderer.PickUpHint, labels[0].Sub);
        Assert.Equal(string.Empty, labels[1].Sub);
        Assert.Equal("Torch", labels[0].Name);
    }

    [Fact]
    public void AnItemAnotherPlayerClaimedIsGreyedUntilTheClaimRunsOut()
    {
        ClientGroundItemCache ground = CacheWith(At(1, 1f, 0f, claimant: 9, expires: 100));

        WorldEntityLabel held = Labels(ground, new ClientNpcCache(), localActor: 2, tick: 50).Single();
        WorldEntityLabel mine = Labels(ground, new ClientNpcCache(), localActor: 9, tick: 50).Single();
        WorldEntityLabel expired = Labels(ground, new ClientNpcCache(), localActor: 2, tick: 100).Single();

        Assert.True(held.Dim);
        Assert.Equal(WorldEntityRenderer.ClaimedHint, held.Sub);
        Assert.False(mine.Dim);
        Assert.False(expired.Dim);
    }

    [Fact]
    public void AnNpcLabelShowsFromFurtherOff_AndTalksWithinReach()
    {
        int id = ReplicatedActorIdRanges.FirstNpcId;

        WorldEntityLabel near = Labels(new ClientGroundItemCache(), NpcAt(id, 4f, 0f)).Single();
        WorldEntityLabel far = Labels(new ClientGroundItemCache(), NpcAt(id, 20f, 0f)).Single();

        Assert.Equal(new HudLabelClick(HudLabelClickKind.InteractNpc, id), near.Click);
        Assert.Equal(WorldEntityRenderer.TalkHint, near.Sub);
        Assert.Equal(string.Empty, far.Sub);
        Assert.Empty(Labels(new ClientGroundItemCache(), NpcAt(id, 60f, 0f)));
    }

    // M5: villagers are forged models now (NpcModelRenderer), so only the ground items box.
    [Fact]
    public void TheBoxesAreOneForAnItem_NoneForAnNpc_AndFarOnesAreSkipped()
    {
        List<WorldBox> boxes = new();
        ClientGroundItemCache ground = CacheWith(At(1, 3f, 3f), At(2, 500f, 0f));
        ClientNpcCache npcs = NpcAt(ReplicatedActorIdRanges.FirstNpcId, 5f, 0f);

        WorldEntityRenderer.CollectBoxes(ground, npcs, Vector3.Zero, boxes);

        Assert.Equal(1, boxes.Count);
        Assert.All(boxes, b => Assert.True(float.IsFinite(b.Center.Y) && b.Size.X > 0f));
    }

    // ---- the label panel ----

    private static HudView ViewWith(params HudWorldLabel[] labels) => new() { InventoryOpen = true, Labels = labels };

    private sealed class PickActions : IHudActions
    {
        public List<long> Pickups { get; } = new();

        public List<int> Talks { get; } = new();

        public void Inventory(InventoryHudAction action)
        {
        }

        public void CloseInventory()
        {
        }

        public void PickUpGround(long itemInstanceId) => Pickups.Add(itemInstanceId);

        public void InteractNpc(int npcActorId) => Talks.Add(npcActorId);
    }

    private void ClickAt(Vector2 at, HudView view, PickActions actions)
    {
        _scene.Input.Pointer = at;
        _scene.Input.LeftClick = true;
        WorldLabelsPanel.Build(_scene.Canvas, _scene.Input, view, _scene.Layout, actions);
        _scene.Input.EndFrame();
    }

    [Fact]
    public void ClickingAGroundLabelPicksItUp_AndClickingAnNpcLabelTalks()
    {
        PickActions actions = new();
        HudView view = ViewWith(
            new HudWorldLabel(new Vector2(300f, 200f), "Torch", "[E] Pick up", new HudLabelClick(HudLabelClickKind.PickUpGround, 77)),
            new HudWorldLabel(new Vector2(700f, 200f), "Mara", "", new HudLabelClick(HudLabelClickKind.InteractNpc, ReplicatedActorIdRanges.FirstNpcId)));

        ClickAt(new Vector2(300f, 205f), view, actions);
        ClickAt(new Vector2(700f, 205f), view, actions);
        ClickAt(new Vector2(500f, 205f), view, actions);

        Assert.Equal([77L], actions.Pickups);
        Assert.Equal([ReplicatedActorIdRanges.FirstNpcId], actions.Talks);
    }

    [Fact]
    public void AnActorLabelHasNoClick_AndALabelWithoutInputIsPlainText()
    {
        PickActions actions = new();
        HudView plain = ViewWith(new HudWorldLabel(new Vector2(300f, 200f), "Wolf", "Lv 1  10/10"));

        ClickAt(new Vector2(300f, 205f), plain, actions);
        Assert.Empty(actions.Pickups);
        Assert.Empty(actions.Talks);

        InventoryFakeCanvas canvas = new(_scene.Table);
        HudView clickable = ViewWith(
            new HudWorldLabel(new Vector2(300f, 200f), "Torch", "", new HudLabelClick(HudLabelClickKind.PickUpGround, 1)));
        WorldLabelsPanel.Build(canvas, clickable, _scene.Layout);

        Assert.DoesNotContain(canvas.Calls, c => c.Kind != CanvasCallKind.Text);
    }

    [Fact]
    public void ADimLabelIsGreyAndATierLabelTakesItsTierColour()
    {
        InventoryFakeCanvas canvas = new(_scene.Table);
        PickActions actions = new();
        HudView view = ViewWith(
            new HudWorldLabel(new Vector2(300f, 200f), "Dim", "", new HudLabelClick(HudLabelClickKind.PickUpGround, 1), ItemTier.Rare, Dim: true),
            new HudWorldLabel(new Vector2(700f, 200f), "Rare", "", new HudLabelClick(HudLabelClickKind.PickUpGround, 2), ItemTier.Rare));

        WorldLabelsPanel.Build(canvas, _scene.Input, view, _scene.Layout, actions);

        Assert.Equal(ItemTierStyle.Dim, canvas.Texts.Last(t => t.Text == "Dim").Color);
        Assert.Equal(ItemTierStyle.Rare, canvas.Texts.Last(t => t.Text == "Rare").Color);
    }

    // ---- the wallet and the loot lines ----

    [Fact]
    public void TheInventoryShowsTheWallet()
    {
        _scene.Refresh();
        _scene.View.Inventory = _scene.View.Inventory!.Value with { Gold = 120 };

        _scene.Frame();

        Assert.True(_scene.Canvas.HasText("Gold 120"));
    }

    [Fact]
    public void AGoldOnlyChangeUpdatesTheViewWithoutCountingAsAnInventoryChange()
    {
        HudView view = new();
        HudViewBuilder builder = new(_scene.Table, view);
        HudStatusSnapshot status = default;
        PlayerInventorySnapshot first = new("p", 8, 8, []);

        builder.Refresh(in status, first);
        builder.Refresh(in status, first with { Gold = 40 });

        Assert.False(builder.InventoryChanged);
        Assert.Equal(40, view.Inventory!.Value.Gold);

        builder.Refresh(in status, first with { Gold = 40 });
        Assert.Equal(40, view.Inventory!.Value.Gold);
    }

    [Fact]
    public void LootLinesNameTheGroundAndLeaveCoinWithoutARarity()
    {
        Assert.Equal(
            "Backpack full: Torch dropped on the ground",
            LootFeedbackSystem.Format(new LootNotice("Torch", ItemRarity.Common, Lost: true)));
        Assert.Equal("Looted: 12 gold", LootFeedbackSystem.Format(new LootNotice("12 gold", ItemRarity.Common, Lost: false)));
        Assert.Equal("Looted: Torch (Rare)", LootFeedbackSystem.Format(new LootNotice("Torch", ItemRarity.Rare, Lost: false)));
    }
}
