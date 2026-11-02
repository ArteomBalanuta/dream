using System.Reflection;
using ImGuiNET;
using Xunit;
using Dream.Client.Scenes;
using Dream.Multiplayer.Protocol;
using Dream.Rendering.Core;
using Dream.Rendering.Hud;
using Dream.Rendering.Ui;
using Dream.Simulation.Inventory;
using Dream.Simulation.Persistence;
using Dream.Simulation.Players;
using Dream.Tests.Simulation.Inventory;
using Dream.Tests.Support;

namespace Dream.Tests.Rendering.Hud;

// Classic HUD iteration 2 verification: the ImGui inventory (the fallback surface when hud.png is missing, and
// the F3 alternative) on the sparse 8x8 model. The real draw code runs in a headless ImGui context with a
// scripted mouse; nothing is mocked in the panel. A real-GL click run was not possible (OpenTK reads the
// polled pointer, and posted window messages do not reach it), so this is the closest drive of the real
// ImGui buttons. The menus (Move To, Destroy) are checked through the model the draw code calls, with every
// entry sent to the real server service.
public sealed class ImGuiFallbackInventoryTests
{
    private const string Player = "Alice";

    private static readonly MethodInfo DrawOverlay = typeof(Render).GetMethod(
        "DrawInventoryOverlay", BindingFlags.NonPublic | BindingFlags.Static)!;

    private static HudStatusSnapshot Status() =>
        new(100, 100, PlayerInteractionMode.Combat, 1, 2, 10, 2f, 10, 1, 0, 100, 10, 2f, 10, 0);

    private static PlayerInventoryService NewService(out PersistenceWriteQueue writes, out EquipmentTestWorld world)
    {
        world = new EquipmentTestWorld();
        PlayerInventoryService service = EquipmentSlotTests.Create(world, out writes);
        Assert.True(service.Ensure(Player));
        Assert.Equal(InventoryEquipOutcome.Equipped, service.TryGrantRolled(Player, "gear.hunting-spear", ItemRarity.Common, new ItemRollSeed(1, 1, 1, 1, 1)));
        Assert.Equal(InventoryEquipOutcome.Equipped, service.TryGrantRolled(Player, "gear.iron-band", ItemRarity.Common, new ItemRollSeed(2, 2, 2, 2, 2)));
        return service;
    }

    private sealed class Pad : IDisposable
    {
        private readonly IntPtr _context;
        private readonly PlayerInventorySnapshot _snapshot;

        public Pad(PlayerInventorySnapshot snapshot)
        {
            _snapshot = snapshot;
            _context = ImGui.CreateContext();
            ImGui.SetCurrentContext(_context);
            ImGuiIOPtr io = ImGui.GetIO();
            io.DisplaySize = new System.Numerics.Vector2(1000f, 2400f);
            UiFontSetup.AddFonts(io);
            io.Fonts.GetTexDataAsRGBA32(out IntPtr _, out int _, out int _, out int _);
        }

        public int? EquipSlot;
        public InventoryHudAction? Action;

        public void Frame(float x, float y, bool left)
        {
            ImGuiIOPtr io = ImGui.GetIO();
            io.DeltaTime = 1f / 60f;
            io.AddMousePosEvent(x, y);
            io.AddMouseButtonEvent(0, left);
            ImGui.NewFrame();
            object?[] args = [_snapshot, Status(), null, null];
            DrawOverlay.Invoke(null, args);
            if (args[2] is int slot)
            {
                EquipSlot = slot;
            }

            if (args[3] is InventoryHudAction action)
            {
                Action = action;
            }

            ImGui.Render();
        }

        public int? Click(float x, float y)
        {
            EquipSlot = null;
            Frame(-1000f, -1000f, false);
            Frame(x, y, false);
            Frame(x, y, false);
            Frame(x, y, true);
            Frame(x, y, true);
            Frame(x, y, false);
            Frame(x, y, false);
            return EquipSlot;
        }

        public void Dispose()
        {
            ImGui.DestroyContext(_context);
        }
    }

    [Fact]
    public void TheImGuiBackpack_HasOneButtonPerItemAnchor_OnTheSparseBoard_AndAClickEquipsThatAnchor()
    {
        PlayerInventoryService service = NewService(out PersistenceWriteQueue writes, out _);
        using (writes)
        {
            PlayerInventorySnapshot snapshot = service.BuildSnapshot(Player);
            int[] anchors = [.. snapshot.BagItems().Select(s => s.SlotIndex)];
            Assert.True(anchors.Length >= 6);
            Assert.True(snapshot.Slots.Count(s => s.Item != null) < 64, "the model is sparse: only anchored items are listed");

            lock (ImGuiTestGate.Gate)
            {
                using Pad pad = new(snapshot);
                pad.Frame(-1000f, -1000f, false);
                pad.Frame(-1000f, -1000f, false);
                HashSet<int> found = [];
                for (float y = 40f; y < 1500f; y += 10f)
                {
                    for (float x = 40f; x < 700f; x += 24f)
                    {
                        if (pad.Click(x, y) is int slot)
                        {
                            found.Add(slot);
                        }
                    }
                }

                Assert.Equal(anchors.Order(), found.Order());
            }
        }
    }

    [Fact]
    public void EveryEntryTheImGuiMenusOffer_IsAcceptedByTheServer_OnTheSparseBoard_MoveEquipUnequipAndDestroy()
    {
        PlayerInventoryService probe = NewService(out PersistenceWriteQueue probeWrites, out _);
        PlayerInventorySnapshot snapshot;
        using (probeWrites)
        {
            snapshot = probe.BuildSnapshot(Player);
        }

        int moves = 0;
        int equips = 0;
        int destroys = 0;
        foreach (InventorySlotSnapshot slot in snapshot.BagItems().ToList())
        {
            InventoryItemSnapshot item = slot.Item!.Value;
            List<InventoryHudAction> actions = [];
            foreach (CharacterMenuEntry entry in CharacterPanelModel.BackpackMenu(snapshot, slot.SlotIndex, item))
            {
                actions.Add(entry.Action);
                if (entry.Action.Kind == InventoryHudActionKind.Destroy)
                {
                    Assert.True(entry.RequiresConfirm);
                }
            }

            actions.AddRange(CharacterPanelModel.MoveTargets(snapshot, slot.SlotIndex));
            Assert.Contains(actions, a => a.Kind == InventoryHudActionKind.Destroy);
            foreach (InventoryHudAction action in actions)
            {
                // A fresh server per action: each is judged on the same starting inventory.
                PlayerInventoryService service = NewService(out PersistenceWriteQueue writes, out _);
                using (writes)
                {
                    PlayerInventorySnapshot start = service.BuildSnapshot(Player);
                    Assert.Equal(snapshot.Layout(), start.Layout());
                    InventoryEquipOutcome outcome = PlayScene.ToInventoryIntent(in action) switch
                    {
                        InventoryMoveIntent m => service.TryMove(Player, m.From, m.To),
                        InventoryEquipIntent e => service.TryEquip(Player, e.SlotIndex, e.TargetSlotIndex),
                        InventoryDestroyIntent d => service.TryDestroy(Player, d.SlotIndex, d.ItemInstanceId),
                        InventoryUnequipIntent u => service.TryUnequip(Player, u.SlotIndex, u.TargetCell),
                        // A potion's "Use" entry: the item.use path consumes it (the cooldown and the pool check are the runtime's).
                        ItemUseIntent use => service.TryConsume(Player, use.SlotIndex, use.ItemInstanceId),
                        _ => throw new InvalidOperationException("unexpected intent")
                    };
                    Assert.True(
                        outcome == InventoryEquipOutcome.Equipped,
                        $"{action} on {item.ItemKind} at {slot.SlotIndex} gave {outcome}");
                    Assert.True(service.BuildSnapshot(Player).BoardIsConsistent());
                    switch (action.Kind)
                    {
                        case InventoryHudActionKind.Move: moves++; break;
                        case InventoryHudActionKind.EquipTo: equips++; break;
                        case InventoryHudActionKind.Destroy: destroys++; break;
                    }
                }
            }
        }

        Assert.True(moves > 20, $"{moves} moves");
        Assert.True(equips >= 5, $"{equips} equips");
        Assert.Equal(snapshot.BagItems().Count(), destroys);
    }
}
