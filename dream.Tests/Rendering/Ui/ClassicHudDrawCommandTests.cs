using Dream.Rendering.Hud;
using Dream.Rendering.Ui;
using Dream.Rendering.Ui.Panels;
using Dream.Simulation.Inventory;
using Dream.Tests.Rendering.Ui.Panels;
using Dream.Tests.Support;
using OpenTK.Mathematics;
using Xunit;
using Xunit.Abstractions;

namespace Dream.Tests.Rendering.Ui;

// The plan's draw budget for the classic HUD: at most 12 commands in play, 16 with windows and a
// tooltip. Commands merge by texture page and scissor in draw order, so the panels draw all of one
// page together where z-order allows.
public class ClassicHudDrawCommandTests
{
    public const int PlayBudget = 12;
    public const int InventoryBudget = 16;

    private readonly ITestOutputHelper _output;

    public ClassicHudDrawCommandTests(ITestOutputHelper output) => _output = output;

    private sealed class NoActions : IHudActions
    {
        public void Inventory(InventoryHudAction action) { }

        public void CloseInventory() { }
    }

    private static InventoryScene Populated()
    {
        InventoryScene scene = new();
        ItemRarity[] tiers = [ItemRarity.Common, ItemRarity.Uncommon, ItemRarity.Rare];
        string[] kinds = ["tool.torch", "gear.iron-blade", "gear.leather-cap", "gear.leather-vest", "gear.ward-lantern", "tool.pickaxe"];
        for (int slot = 0; slot < kinds.Length; slot++)
        {
            scene.Items[slot] = InventoryScene.Item(slot + 1, kinds[slot], rarity: tiers[slot % 3], stack: slot == 5 ? 12 : 1);
        }

        scene.Worn[0] = scene.Items[1];
        scene.Equipped[1] = true;
        scene.Refresh();
        scene.View.Status = new HudStatusSnapshot(
            73, 120, Dream.Simulation.Players.PlayerInteractionMode.Combat, 5, 9, 12, 1.2f, 14, 7, 40, 100, 10, 1f, 10, 3, false, 0);
        scene.View.StatRows = CharacterPanelModel.BuildStatRows(scene.View.Status);
        scene.View.PlayerPosition = new Vector3(142.6f, 12f, -87.2f);
        return scene;
    }

    private UiBatch Build(InventoryScene scene, int w, int h, UiPointer[] frames)
    {
        (UiGlyphTable glyphs, _) = RealFont.Get();
        UiLayout layout = UiLayout.Create(w, h, 0f);
        UiBatch batch = new(glyphs);
        batch.SetSpriteTable(scene.Table);
        UiInputRouter router = new();
        NoActions actions = new();
        foreach (UiPointer p in frames)
        {
            batch.BeginFrame(in layout);
            router.BeginFrame(p);
            ClassicHud.Build(batch, router, scene.View, in layout, scene.Table, actions);
            router.EndFrame();
        }

        foreach (UiDrawCommand cmd in batch.Commands)
        {
            _output.WriteLine($"{cmd.Page} quads={cmd.IndexCount / 6} clip={cmd.Clip} free={cmd.PageFree}");
        }

        return batch;
    }

    [Theory]
    [InlineData(1280, 720)]
    [InlineData(1920, 1080)]
    [InlineData(1440, 1024)]
    public void Play_StaysInsideTheBudget(int w, int h)
    {
        using InventoryScene scene = Populated();
        scene.View.InventoryOpen = false;

        UiBatch batch = Build(scene, w, h, [new UiPointer(Vector2.Zero, false, false, false)]);

        _output.WriteLine($"play commands={batch.CommandCount}");
        Assert.True(batch.CommandCount <= PlayBudget, $"{batch.CommandCount} commands in play, budget {PlayBudget}");
    }

    [Theory]
    [InlineData(1280, 720)]
    [InlineData(1920, 1080)]
    [InlineData(1440, 1024)]
    public void InventoryWithTooltip_StaysInsideTheBudget(int w, int h)
    {
        using InventoryScene scene = Populated();
        UiLayout layout = UiLayout.Create(w, h, 0f);
        Vector2 hover = InventoryScene.Centre(InventoryWindow.SlotRect(layout, layout.Inventory, 1));
        UiPointer p = new(hover, false, false, true);

        UiBatch batch = Build(scene, w, h, [p, p, p]);

        _output.WriteLine($"inventory+tooltip commands={batch.CommandCount} quads={batch.QuadCount}");
        Assert.True(InventoryUiState.Current.HoverActive);
        Assert.True(batch.CommandCount <= InventoryBudget, $"{batch.CommandCount} commands with windows and a tooltip, budget {InventoryBudget}");
    }
}
