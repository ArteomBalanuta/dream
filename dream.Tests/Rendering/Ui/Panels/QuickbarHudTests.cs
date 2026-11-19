using Dream.Multiplayer.Protocol;
using Dream.Rendering.Hud;
using Dream.Rendering.Ui;
using Dream.Rendering.Ui.Panels;
using Dream.Simulation.Content;
using Dream.Simulation.Inventory;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Tests.Utilities;
using Xunit;

namespace Dream.Tests.Rendering.Ui.Panels;

// M4: what the quickbar state keeps from the wire, and what the six tiles draw and report.
public sealed class QuickbarHudTests
{
    private const string Health = "potion.health";
    private const string Mana = "potion.mana";

    private static InventoryItemSnapshot Potion(long id, string kind) =>
        new(id, kind, kind, "visual", 1, string.Empty, []);

    private static PlayerInventorySnapshot Bag(IReadOnlyList<QuickbarSlotSnapshot>? quickbar, params (int Anchor, long Id, string Kind)[] items) =>
        new(
            "p",
            InventoryConstants.GridColumns,
            InventoryConstants.GridRows,
            [.. items.Select(i => new InventorySlotSnapshot(i.Anchor, Potion(i.Id, i.Kind)))],
            Quickbar: quickbar);

    private static QuickbarSlotSnapshot Bind(int slot, QuickbarEntryKind kind, string key) => new(slot, kind, key);

    // ---- the state ----

    [Fact]
    public void ANewState_ShowsTheContentDefault_WithNoManaAndNoCooldowns()
    {
        QuickbarHudState bar = new();

        Assert.Equal(QuickbarEntryKind.Skill, bar.Slot(0).Kind);
        Assert.Equal("skill.cleave", bar.Slot(0).Key);
        Assert.Equal("skill.bolt", bar.Slot(1).Key);
        Assert.Equal("skill.mend", bar.Slot(2).Key);
        Assert.True(bar.IsEmpty(3));
        Assert.Equal((QuickbarEntryKind.Item, Health), (bar.Slot(4).Kind, bar.Slot(4).Key));
        Assert.Equal((QuickbarEntryKind.Item, Mana), (bar.Slot(5).Kind, bar.Slot(5).Key));
        Assert.Equal(0, bar.MaxMana);
        Assert.Equal(0, bar.CooldownCount);
        Assert.False(bar.HasData);
    }

    [Fact]
    public void ANullQuickbarInTheSnapshot_KeepsTheDefault_AndABoundOneReplacesIt()
    {
        QuickbarHudState bar = new();
        bar.ApplyInventory(Bag(null));
        Assert.Equal("skill.cleave", bar.Slot(0).Key);
        Assert.True(bar.HasData);

        bar.ApplyInventory(Bag(
        [
            Bind(0, QuickbarEntryKind.None, string.Empty),
            Bind(1, QuickbarEntryKind.Skill, "skill.mend"),
            Bind(2, QuickbarEntryKind.Item, Mana),
            Bind(3, QuickbarEntryKind.None, string.Empty),
            Bind(4, QuickbarEntryKind.None, string.Empty),
            Bind(5, QuickbarEntryKind.None, string.Empty)
        ]));

        Assert.True(bar.IsEmpty(0));
        Assert.Equal("skill.mend", bar.Slot(1).Key);
        Assert.True(bar.IsHeal(1));
        Assert.Equal(Mana, bar.Slot(2).Key);
        Assert.True(bar.IsEmpty(4));
        Assert.True(bar.IsEmpty(5));
    }

    [Fact]
    public void AnAllNoneBar_DoesNotFallBackToTheDefault()
    {
        QuickbarHudState bar = new();
        bar.ApplyInventory(Bag([.. Enumerable.Range(0, 6).Select(s => Bind(s, QuickbarEntryKind.None, string.Empty))]));

        for (int slot = 0; slot < 6; slot++)
        {
            Assert.True(bar.IsEmpty(slot));
        }
    }

    [Fact]
    public void HostileBindings_ShowAsEmptyTiles_AndNeverThrow()
    {
        QuickbarHudState bar = new();
        bar.ApplyInventory(Bag(
        [
            Bind(-1, QuickbarEntryKind.Skill, "skill.cleave"),
            Bind(6, QuickbarEntryKind.Skill, "skill.cleave"),
            Bind(0, QuickbarEntryKind.Skill, "skill.nope"),
            Bind(1, QuickbarEntryKind.Item, "wolf.pelt.nope"),
            Bind(2, QuickbarEntryKind.Item, "tool.torch"),
            Bind(3, (QuickbarEntryKind)9, "skill.bolt"),
            Bind(4, QuickbarEntryKind.Skill, new string('x', 5000)),
            Bind(5, QuickbarEntryKind.Skill, null!)
        ]));

        for (int slot = 0; slot < 6; slot++)
        {
            Assert.True(bar.IsEmpty(slot), $"slot {slot}");
        }

        Assert.True(bar.IsEmpty(-4));
        Assert.True(bar.IsEmpty(99));
        Assert.Equal(0, bar.ItemCount(99));
    }

    [Fact]
    public void ItemCounts_AreTheBagAnchorsOfTheBoundKind_AndTheLowestAnchorIsUsed()
    {
        QuickbarHudState bar = new();
        PlayerInventorySnapshot inventory = new(
            "p",
            InventoryConstants.GridColumns,
            InventoryConstants.GridRows,
            [
                new InventorySlotSnapshot(20, Potion(7, Health)),
                new InventorySlotSnapshot(3, Potion(5, Health)),
                new InventorySlotSnapshot(9, Potion(6, Mana)),
                // Held back (200+) and worn items are not in the bag.
                new InventorySlotSnapshot(200, Potion(8, Health)),
                new InventorySlotSnapshot(30, Potion(9, Health), IsEquipped: true)
            ]);
        bar.ApplyInventory(inventory);

        Assert.Equal(2, bar.ItemCount(4));
        Assert.Equal(1, bar.ItemCount(5));
        Assert.Equal(0, bar.ItemCount(0));
        Assert.True(bar.TryGetItemTarget(4, out int slot, out long id));
        Assert.Equal((3, 5L), (slot, id));
        Assert.True(bar.TryGetItemTarget(5, out slot, out id));
        Assert.Equal((9, 6L), (slot, id));
        Assert.False(bar.TryGetItemTarget(0, out _, out _));

        bar.ApplyInventory(Bag(null));
        Assert.Equal(0, bar.ItemCount(4));
        Assert.False(bar.TryGetItemTarget(4, out _, out _));
        Assert.True(bar.IsUnavailable(4));
    }

    [Fact]
    public void Vitals_AreClamped_AndTheCooldownListIsCapped()
    {
        QuickbarHudState bar = new();
        bar.ApplyVitals(new ServerPlayerVitals(500, 60, []));
        Assert.Equal((60, 60), (bar.CurrentMana, bar.MaxMana));

        bar.ApplyVitals(new ServerPlayerVitals(-5, 60, []));
        Assert.Equal(0, bar.CurrentMana);

        bar.ApplyVitals(new ServerPlayerVitals(int.MaxValue, int.MaxValue, []));
        Assert.Equal((QuickbarHudState.MaxManaCap, QuickbarHudState.MaxManaCap), (bar.CurrentMana, bar.MaxMana));

        bar.ApplyVitals(new ServerPlayerVitals(5, int.MinValue, []));
        Assert.Equal((0, 0), (bar.CurrentMana, bar.MaxMana));

        List<AbilityCooldown> many = [.. Enumerable.Range(0, 100).Select(n => new AbilityCooldown($"k{n}", 100))];
        many.Insert(0, new AbilityCooldown(string.Empty, 5));
        many.Insert(1, new AbilityCooldown(new string('y', 500), 5));
        bar.ApplyVitals(new ServerPlayerVitals(5, 60, many));
        Assert.Equal(ServerPlayerVitals.MaxCooldowns - 2, bar.CooldownCount);

        bar.ApplyVitals(null);
        Assert.Equal(ServerPlayerVitals.MaxCooldowns - 2, bar.CooldownCount);
    }

    [Fact]
    public void Cooldowns_AreFoundByKey_IgnoringCase_AndCountDownToTheServerTick()
    {
        QuickbarHudState bar = new();
        bar.ApplyVitals(new ServerPlayerVitals(50, 50, [new AbilityCooldown("SKILL.CLEAVE", 1000), new AbilityCooldown(Health, 900)]));
        ContentCatalog.Shipped.TryGetSkill("skill.cleave", out SkillDefinition cleave);

        ulong remaining = bar.RemainingTicks(0, 940, out int total);
        Assert.Equal(60UL, remaining);
        Assert.Equal(cleave.CooldownTicks, total);
        Assert.Equal(0UL, bar.RemainingTicks(0, 1000, out _));
        Assert.Equal(0UL, bar.RemainingTicks(0, 5000, out _));
        Assert.Equal(0UL, bar.RemainingTicks(1, 940, out _));
        Assert.Equal(40UL, bar.RemainingTicks(4, 860, out int itemTotal));
        Assert.Equal(320, itemTotal);
        Assert.Equal(0UL, bar.RemainingTicks(3, 0, out _));
    }

    [Fact]
    public void ASkill_IsUnavailable_WhenItCostsMoreManaThanTheOwnerHas()
    {
        QuickbarHudState bar = new();
        ContentCatalog.Shipped.TryGetSkill("skill.cleave", out SkillDefinition cleave);
        bar.ApplyVitals(new ServerPlayerVitals(cleave.ManaCost - 1, 50, []));
        Assert.True(bar.IsUnavailable(0));

        bar.ApplyVitals(new ServerPlayerVitals(cleave.ManaCost, 50, []));
        Assert.False(bar.IsUnavailable(0));
    }

    [Fact]
    public void Clear_ForgetsTheSession()
    {
        QuickbarHudState bar = new();
        bar.ApplyVitals(new ServerPlayerVitals(30, 50, [new AbilityCooldown("skill.bolt", 99)]));
        bar.ApplyInventory(Bag([.. Enumerable.Range(0, 6).Select(s => Bind(s, QuickbarEntryKind.None, string.Empty))], (4, 1, Health)));
        bar.RequestCycle(2);
        Assert.True(bar.HasData);

        bar.Clear();

        Assert.False(bar.HasData);
        Assert.Equal((0, 0, 0), (bar.CurrentMana, bar.MaxMana, bar.CooldownCount));
        Assert.Equal("skill.cleave", bar.Slot(0).Key);
        Assert.Equal(0, bar.ItemCount(4));
        Assert.Equal(-1, bar.TakeCycleRequest());
    }

    [Fact]
    public void TheCycle_GoesSkillsInContentOrder_ThenBagConsumables_ThenNone_ThenRoundAgain()
    {
        QuickbarHudState bar = new();
        bar.ApplyInventory(Bag(
            [.. Enumerable.Range(0, 6).Select(s => Bind(s, QuickbarEntryKind.None, string.Empty))],
            (0, 1, Mana),
            (1, 2, Health),
            (2, 3, Mana)));
        string[] skills = [.. ContentCatalog.Shipped.Skills.Select(s => s.Key)];

        List<(QuickbarEntryKind Kind, string Key)> seen = [];
        QuickbarSlotSnapshot entry = bar.Slot(3);
        for (int step = 0; step < skills.Length + 4; step++)
        {
            entry = bar.NextEntry(3);
            seen.Add((entry.Kind, entry.Key));
            bar.ApplyInventory(Bag(
                [.. Enumerable.Range(0, 6).Select(s => s == 3 ? Bind(3, entry.Kind, entry.Key) : Bind(s, QuickbarEntryKind.None, string.Empty))],
                (0, 1, Mana),
                (1, 2, Health),
                (2, 3, Mana)));
        }

        List<(QuickbarEntryKind, string)> expected =
        [
            .. skills.Select(k => (QuickbarEntryKind.Skill, k)),
            (QuickbarEntryKind.Item, Mana),
            (QuickbarEntryKind.Item, Health),
            (QuickbarEntryKind.None, string.Empty),
            (QuickbarEntryKind.Skill, skills[0])
        ];
        Assert.Equal(expected, seen);
    }

    [Fact]
    public void TheCycle_StartsAtTheFirstSkill_WhenTheBoundItemIsNoLongerInTheBag()
    {
        QuickbarHudState bar = new();
        bar.ApplyInventory(Bag(null));

        // Slot 4 holds the health potion kind by default, with none in the bag.
        QuickbarSlotSnapshot next = bar.NextEntry(4);

        Assert.Equal((QuickbarEntryKind.Skill, ContentCatalog.Shipped.Skills[0].Key), (next.Kind, next.Key));
    }

    // ---- the panel ----

    private static HudView View(QuickbarHudState? bar, bool inventoryOpen = false, PlayerInteractionMode mode = PlayerInteractionMode.Combat)
    {
        HudView v = StatusBarTestData.View(mode: mode);
        v.Quickbar = bar;
        v.InventoryOpen = inventoryOpen;
        v.ManaLevel = 1f;
        v.HealthLevel = 1f;
        return v;
    }

    private static StatusBarRecordingCanvas Run(HudView v, UiLayout l, StatusBarFakeInput? input = null, IHudActions? actions = null, bool recording = true)
    {
        StatusBarRecordingCanvas c = new(StatusBarTestData.Table, recording);
        StatusBarPanel.Build(c, input ?? new StatusBarFakeInput(), v, l, StatusBarTestData.Table, actions ?? new StatusBarFakeActions());
        return c;
    }

    private static UiLayout Layout() => UiLayout.Create(1920, 1080, 0f);

    private static bool Inside(UiRect outer, UiRect inner) =>
        inner.X >= outer.X && inner.Y >= outer.Y && inner.Right <= outer.Right + 0.01f && inner.Bottom <= outer.Bottom + 0.01f;

    [Fact]
    public void WithoutAState_TheBarIsDrawnAsItWas_NoTilesNoDigits()
    {
        UiLayout l = Layout();
        StatusBarRecordingCanvas without = Run(View(null), l);

        Assert.DoesNotContain(without.Texts, t => t.Text is "1" or "2" or "3" or "4" or "5" or "6");
        Assert.DoesNotContain(without.Calls, c => c.Kind == StatusBarCallKind.Solid && c.Color.A == 235);
    }

    [Fact]
    public void ThreeSkillsAndTwoItems_AreDrawnInsideTheirSockets_WithTheirDigits()
    {
        UiLayout l = Layout();
        QuickbarHudState bar = new();
        bar.ApplyInventory(Bag(null, (0, 1, Health), (1, 2, Health), (2, 3, Mana)));
        StatusBarRecordingCanvas c = Run(View(bar), l);
        UiRect group = l.CentreGroup;

        for (int slot = 0; slot < 6; slot++)
        {
            Assert.True(Inside(group, QuickbarPanel.TileRect(l, slot)), $"tile {slot} leaves the centre group");
            Assert.Contains(c.Texts, t => t.Text == (slot + 1).ToString());
        }

        // Bound tiles have a recess: one per non-empty slot (slot 4 is empty by default).
        Assert.Equal(5, c.Calls.Count(x => x.Kind == StatusBarCallKind.Solid && x.Color.A == 235 && x.Color.R == 0x0C));
        // Two health potions, one mana potion.
        Assert.Contains(c.Texts, t => t.Text == "2");
        Assert.Contains(c.Texts, t => t.Text == "1");
        // Cleave has its own sprite (QuickbarCleaveIconTests); Bolt and Mend have no art yet, so two letters stand in.
        Assert.DoesNotContain(c.Texts, t => t.Text == "Cl");
        Assert.Contains(c.Texts, t => t.Text == "Bo");
        Assert.Contains(c.Texts, t => t.Text == "Me");
    }

    [Fact]
    public void ARunningCooldown_VeilsTheTileFromTheTop_AndShowsTheSecondsLeft()
    {
        UiLayout l = Layout();
        QuickbarHudState bar = new();
        ContentCatalog.Shipped.TryGetSkill("skill.cleave", out SkillDefinition cleave);
        bar.ApplyVitals(new ServerPlayerVitals(50, 50, [new AbilityCooldown(cleave.Key, 1000)]));
        bar.NowTick = 1000 - (ulong)(cleave.CooldownTicks / 2);
        UiRect tile = QuickbarPanel.TileRect(l, 0);

        StatusBarRecordingCanvas c = Run(View(bar), l);

        StatusBarCall veil = Assert.Single(c.Calls, x => x.Kind == StatusBarCallKind.Solid && x.Color.A == 190);
        Assert.Equal(tile.X, veil.Rect.X);
        Assert.Equal(tile.Y, veil.Rect.Y);
        Assert.Equal(tile.W, veil.Rect.W);
        Assert.InRange(veil.Rect.H, tile.H * 0.45f, tile.H * 0.55f);
        int seconds = (int)Math.Ceiling(cleave.CooldownTicks / 2d / SimulationClock.AuthoritativeTicksPerSecond);
        Assert.Contains(c.Texts, t => t.Text == seconds.ToString());

        // Ready again: no veil.
        bar.NowTick = 1000;
        Assert.DoesNotContain(Run(View(bar), l).Calls, x => x.Kind == StatusBarCallKind.Solid && x.Color.A == 190);
    }

    [Fact]
    public void ATileThatCannotBeUsed_IsTinted_ASpellInBuildingModeToo_ButMendIsNot()
    {
        UiLayout l = Layout();
        QuickbarHudState bar = new();
        bar.ApplyVitals(new ServerPlayerVitals(1000, 1000, []));
        bar.ApplyInventory(Bag(null, (0, 1, Health)));

        // Plenty of mana, combat mode: the two spent potions' tiles (mana potion missing) are the only tints.
        Assert.Single(Run(View(bar), l).Calls, x => x.Kind == StatusBarCallKind.Solid && x.Color.A == 150);

        // Building mode: Cleave and Bolt cannot be cast; Mend can, so three tints with the missing potion.
        Assert.Equal(3, Run(View(bar, mode: PlayerInteractionMode.Building), l).Calls.Count(x => x.Kind == StatusBarCallKind.Solid && x.Color.A == 150));

        // No mana: all three spells and the missing potion.
        bar.ApplyVitals(new ServerPlayerVitals(0, 1000, []));
        Assert.Equal(4, Run(View(bar), l).Calls.Count(x => x.Kind == StatusBarCallKind.Solid && x.Color.A == 150));
    }

    [Fact]
    public void AClickOnATile_IsReported_OnlyWhileTheInventoryIsOpen()
    {
        UiLayout l = Layout();
        QuickbarHudState bar = new();
        RecordingActions actions = new();
        StatusBarFakeInput input = new() { ClickedId = QuickbarPanel.IdTile + 2 };

        Run(View(bar, inventoryOpen: false), l, input, actions);
        Assert.Empty(actions.Slots);

        Run(View(bar, inventoryOpen: true), l, input, actions);
        Assert.Equal([2], actions.Slots);
    }

    [Fact]
    public void ManaOnTheGem_ShowsAsNumbers_OnceTheServerSentAMaximum()
    {
        UiLayout l = Layout();
        HudView none = View(null);
        Assert.DoesNotContain(Run(none, l).Calls, c => c.Name == "hudnum.3" || c.Text == "3/4");

        HudView v = View(null);
        v.Status = v.Status with { CurrentMana = 3, MaxMana = 4 };
        StatusBarRecordingCanvas c = Run(v, l);
        bool asText = c.Texts.Any(t => t.Text == "3/4");
        bool asSprites = c.Calls.Any(x => x.Name == "hudnum.3") && c.Calls.Any(x => x.Name == "hudnum.4");
        Assert.True(asText || asSprites);
    }

    [Fact]
    public void TheInitials_AreTwoLetters()
    {
        Span<char> buf = stackalloc char[8];
        Assert.Equal(2, QuickbarPanel.WriteInitials(buf, "Cleave"));
        Assert.Equal("Cl", buf[..2].ToString());
        Assert.Equal(2, QuickbarPanel.WriteInitials(buf, "Health Potion"));
        Assert.Equal("HP", buf[..2].ToString());
        Assert.Equal(1, QuickbarPanel.WriteInitials(buf, "x"));
        Assert.Equal(0, QuickbarPanel.WriteInitials(buf, "  "));
        Assert.Equal(0, QuickbarPanel.WriteInitials(buf, null));
    }

    [Fact]
    public void TheQuickbarPanel_AllocatesNothingOnASteadyFrame()
    {
        UiLayout l = Layout();
        QuickbarHudState bar = new();
        bar.ApplyInventory(Bag(null, (0, 1, Health), (1, 2, Mana)));
        bar.ApplyVitals(new ServerPlayerVitals(30, 50, [new AbilityCooldown("skill.cleave", 500)]));
        bar.NowTick = 400;
        HudView v = View(bar);
        StatusBarRecordingCanvas c = new(StatusBarTestData.Table, recording: false);
        StatusBarFakeInput input = new();
        StatusBarFakeActions actions = new();

        // The old measurement warmed 50 frames - short of the tier-up call count once the
        // runtime's tiering delay is counted - and then charged a single 200-frame window against
        // a 4096 B tolerance. A tier-up or OSR transition landing inside that one window is
        // charged to this thread, so the reading was a function of machine speed and suite load,
        // and the tolerance was the only thing hiding it. AllocationAssert warms past tier-up and
        // keeps the cleanest of several steady-state rounds, so the bound can be what the name
        // claims: a steady frame allocates nothing at all.
        AllocationAssert.AtMost(0, () => StatusBarPanel.Build(c, input, v, l, StatusBarTestData.Table, actions));
    }

    private sealed class RecordingActions : IHudActions
    {
        public List<int> Slots { get; } = [];

        public void Inventory(InventoryHudAction action)
        {
        }

        public void CloseInventory()
        {
        }

        public void Quickbar(int slot) => Slots.Add(slot);
    }
}
