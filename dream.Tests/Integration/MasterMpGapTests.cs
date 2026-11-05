using Microsoft.Data.Sqlite;
using OpenTK.Mathematics;
using Xunit;
using Dream.Client.Startup;
using Dream.Multiplayer.Protocol;
using Dream.Rendering.Actors;
using Dream.Rendering.Hud;
using Dream.Server;
using Dream.Server.Hosting;
using Dream.Simulation.Content;
using Dream.Simulation.Inventory;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.Stats;
using Dream.Simulation.World;
using Dream.Tests.Server.Persistence.Migrations;
using Dream.Tests.Support;

namespace Dream.Tests.Integration;

// Gaps found when verifying the master-mp integration: Agility/Energy spends end to end (with a relog),
// the mana potion's cooldown, noclip/freeze gating on a real server, a remote player's held-weapon key
// next to M8's stat stripping, and what a brand-new character can drink with Q and R.
[Collection(TimingSensitiveCollection.Name)]
public sealed class MasterMpGapTests
{
    private const string SessionA = "mp-gap-a";
    private const string SessionB = "mp-gap-b";

    // ---- 1: Agility and Energy spend, end to end, persisted ----

    // A new character is created at 10 in every attribute, the origin points are measured from: spending 4 into
    // Agility and 3 into Energy reads 14 and 13, and 7 of the 10 points level 3 earns (5 * (3 - 1)) leave 3.
    private const int AgilityAfter = PlayerStatRules.NewCharacterAttribute + 4;
    private const int EnergyAfter = PlayerStatRules.NewCharacterAttribute + 3;

    [Fact]
    public async Task SpendingAgilityAndEnergy_ChangesDefenseAttackSpeedAndMaxMana_DropsPoints_AndSurvivesARelog()
    {
        using MigrationTestDatabase db = new();
        int defenseBefore;
        int maxManaBefore;
        float attackSpeedBefore;
        float gearAttackSpeed;

        await using (WireLoopbackServer server = await M4AKit.StartAsync(db))
        {
            await using HeadlessPlayScene a = await M4AKit.JoinAsync(server, SessionA);
            await M4AKit.PlaceAsync(server, a, M4AKit.Away, a);
            PlayerCombatant combatant = M4AKit.Combatant(server, SessionA);

            // A level gain: level 3 earns 10 points, a fresh character has spent none.
            combatant.ApplyProgression(3, 0);
            Assert.Equal(10, PlayerStatRules.PointsAvailable(combatant.Level, combatant.BaseStats));
            defenseBefore = combatant.Defense;
            maxManaBefore = combatant.MaxMana;
            attackSpeedBefore = combatant.Stats.AttackSpeed;
            // The worn starter kit adds attack speed on top of the base 2.0; the cap used to hide that. Recover it
            // from the pre-spend value so the derived-value check below holds exactly.
            gearAttackSpeed = attackSpeedBefore - PlayerStatRules.EffectiveAttackSpeed(combatant.BaseStats.AttackSpeed, PlayerStatRules.NewCharacterAttribute);

            ServerActionMessage agility = await M4AKit.ActAsync(server, a, new PlayerStatSpendIntent(StatId.Agility, 4), a);
            Assert.True(agility.Result.Accepted, agility.Result.Rejection?.Reason);
            ServerActionMessage energy = await M4AKit.ActAsync(server, a, new PlayerStatSpendIntent(StatId.Energy, 3), a);
            Assert.True(energy.Result.Accepted, energy.Result.Rejection?.Reason);
            PlayerStatsUpdateResult update = Assert.IsType<PlayerStatsUpdateResult>(energy.Result.Payload);
            Assert.Equal(AgilityAfter, update.Stats.Agility);
            Assert.Equal(EnergyAfter, update.Stats.Energy);

            Assert.Equal(AgilityAfter, combatant.BaseStats.Agility);
            Assert.Equal(EnergyAfter, combatant.BaseStats.Energy);
            Assert.Equal(AgilityAfter, combatant.Stats.Agility);
            Assert.Equal(EnergyAfter, combatant.Stats.Energy);
            Assert.Equal(PlayerStatRules.Defense(AgilityAfter), combatant.Defense);
            Assert.True(combatant.Defense > defenseBefore, $"defense {defenseBefore} -> {combatant.Defense}");
            // Attack speed is base 2.0 + 0.01 per Agility, clamped to MaxAttackSpeed (5). At the 10 a character is
            // created with that is 2.10, and 4 more Agility make 2.14: a strict rise (+0.04). The cap is about 300
            // Agility away, so Agility's tempo effect is live again (at the 1000 origin it sat on the cap and a
            // spend could not raise it).
            Assert.Equal(PlayerStatRules.EffectiveAttackSpeed(combatant.BaseStats.AttackSpeed + gearAttackSpeed, AgilityAfter), combatant.Stats.AttackSpeed, 4);
            Assert.True(combatant.Stats.AttackSpeed > attackSpeedBefore, $"attack speed {attackSpeedBefore} -> {combatant.Stats.AttackSpeed}");
            // The assertion above recovers the gear term from the observed pre-spend value, so it cannot fail on
            // that term's magnitude. This one is direct: 4 points of Agility move attack speed by exactly
            // 4 * AttackSpeedPerAgility, clamp uninvolved. At the 1000 origin both sides sat pinned at the 5.0 cap
            // and this delta was 0.0, so it is precisely what moving the origin back to 10 restores.
            Assert.Equal(4 * PlayerStatRules.AttackSpeedPerAgility, combatant.Stats.AttackSpeed - attackSpeedBefore, 4);
            Assert.Equal(PlayerManaRules.MaxMana(3, EnergyAfter), combatant.MaxMana);
            Assert.True(combatant.MaxMana > maxManaBefore);
            Assert.Equal(3, PlayerStatRules.PointsAvailable(combatant.Level, combatant.BaseStats));

            // The remaining 3 points are all there is: a 4th is refused and nothing moves.
            ServerActionMessage over = await M4AKit.ActAsync(server, a, new PlayerStatSpendIntent(StatId.Agility, 4), a);
            Assert.False(over.Result.Accepted);
            Assert.Equal(AgilityAfter, combatant.BaseStats.Agility);

            // The owner's HUD hears the new base stats.
            await M4AKit.StepThroughVitalsDrainAsync(server, a);
            Assert.Contains(a.Applied.OfType<ServerPlayerProgress>(), p => p.BaseStats.Agility == AgilityAfter && p.BaseStats.Energy == EnergyAfter);

            await a.LeaveAsync();
            M4AKit.Flush(server);
        }

        SqliteConnection.ClearAllPools();
        await using (WireLoopbackServer again = await M4AKit.StartAsync(db))
        {
            await using HeadlessPlayScene back = await M4AKit.JoinAsync(again, SessionA);
            PlayerCombatant combatant = M4AKit.Combatant(again, SessionA);
            Assert.Equal(3, combatant.Level);
            Assert.Equal(AgilityAfter, combatant.BaseStats.Agility);
            Assert.Equal(EnergyAfter, combatant.BaseStats.Energy);
            Assert.Equal(PlayerStatRules.Defense(AgilityAfter), combatant.Defense);
            Assert.Equal(PlayerManaRules.MaxMana(3, EnergyAfter), combatant.MaxMana);
            Assert.Equal(PlayerStatRules.EffectiveAttackSpeed(combatant.BaseStats.AttackSpeed + gearAttackSpeed, AgilityAfter), combatant.Stats.AttackSpeed, 4);
            Assert.True(combatant.Stats.AttackSpeed < PlayerCombatStats.MaxAttackSpeed, $"attack speed {combatant.Stats.AttackSpeed}");
            Assert.Equal(3, PlayerStatRules.PointsAvailable(combatant.Level, combatant.BaseStats));
            await back.LeaveAsync();
        }
    }

    // ---- 2: the mana potion ----

    [Fact]
    public async Task ManaPotion_RestoresItsManaOnce_AndASecondUseInsideTheCooldownIsRefusedAndKept()
    {
        using MigrationTestDatabase db = new();
        await using WireLoopbackServer server = await M4AKit.StartAsync(db);
        await using HeadlessPlayScene a = await M4AKit.JoinAsync(server, SessionA);
        await M4AKit.PlaceAsync(server, a, M4AKit.Away, a);
        // The kit holds one mana potion (starter kit v4); one bought makes the two this test needs.
        await M4AKit.BuyAsync(server, a, "potion.mana", a);
        InventorySlotSnapshot[] potions = M4AKit.BagKind(a, "potion.mana");
        Assert.Equal(2, potions.Length);

        PlayerCombatant combatant = M4AKit.Combatant(server, SessionA);
        Assert.True(combatant.TrySpendMana(combatant.MaxMana - 5));
        int before = combatant.CurrentMana;

        ServerActionMessage used = await M4AKit.ActAsync(server, a, new ItemUseIntent(potions[0].SlotIndex, potions[0].Item!.Value.ItemInstanceId), a);
        Assert.True(used.Result.Accepted, used.Result.Rejection?.Reason);
        // Restores 40 (items.json), less than the 'max' cap would allow, so it is exactly the potion (plus at most regen).
        Assert.InRange(combatant.CurrentMana, before + 40, combatant.MaxMana);
        Assert.Single(M4AKit.BagKind(a, "potion.mana"));

        // Spend again so a second potion has something to restore: the refusal is the cooldown, not "nothing to restore".
        Assert.True(combatant.TrySpendMana(30));
        int hurt = combatant.CurrentMana;
        InventorySlotSnapshot second = M4AKit.BagKind(a, "potion.mana").Single();
        ServerActionMessage refused = await M4AKit.ActAsync(server, a, new ItemUseIntent(second.SlotIndex, second.Item!.Value.ItemInstanceId), a);
        Assert.False(refused.Result.Accepted);
        Assert.Contains("cooldown", refused.Result.Rejection!.Reason, StringComparison.Ordinal);
        Assert.Single(M4AKit.BagKind(a, "potion.mana"));
        Assert.InRange(combatant.CurrentMana, hurt, hurt + 5);

        await M4AKit.StepManyAsync(server, 330, a);
        int rested = combatant.CurrentMana;
        ServerActionMessage again = await M4AKit.ActAsync(server, a, new ItemUseIntent(second.SlotIndex, second.Item!.Value.ItemInstanceId), a);
        Assert.True(again.Result.Accepted, again.Result.Rejection?.Reason);
        Assert.Empty(M4AKit.BagKind(a, "potion.mana"));
        Assert.True(combatant.CurrentMana >= Math.Min(combatant.MaxMana, rested + 40));
    }

    [Fact]
    public async Task ADeadPlayer_CannotSpendStatPoints()
    {
        using MigrationTestDatabase db = new();
        await using WireLoopbackServer server = await M4AKit.StartAsync(db);
        await using HeadlessPlayScene a = await M4AKit.JoinAsync(server, SessionA);
        await M4AKit.PlaceAsync(server, a, M4AKit.Away, a);
        PlayerCombatant combatant = M4AKit.Combatant(server, SessionA);
        combatant.ApplyProgression(3, 0);
        combatant.Health.ApplyDamage(1_000_000);
        Assert.False(combatant.Health.IsAlive);
        int agilityBefore = combatant.BaseStats.Agility;

        ServerActionMessage refused = await M4AKit.ActAsync(server, a, new PlayerStatSpendIntent(StatId.Agility, 2), a);
        Assert.False(refused.Result.Accepted);
        Assert.Contains("dead", refused.Result.Rejection!.Reason, StringComparison.Ordinal);
        Assert.Equal(agilityBefore, combatant.BaseStats.Agility);
    }

    // ---- 3: dev gating on a real server ----

    [Fact]
    public async Task DedicatedServer_IgnoresNoclipOneShots_AndAServerWithDevCommandsHonoursThem()
    {
        using MigrationTestDatabase db = new();
        await using WireLoopbackServer server = await M4AKit.StartAsync(db);
        await using HeadlessPlayScene a = await M4AKit.JoinAsync(server, SessionA);
        await M4AKit.PlaceAsync(server, a, M4AKit.Away, a);
        Assert.False(server.Host.DevCommandsAllowed);
        Assert.False(server.Runtime.Player.IsNoclipEnabled);

        PlayerMoveIntent toggle = new(0f, 0f, 0f, JumpPressed: false, FreezePressed: true, ToggleNoclipPressed: true);
        await a.SendCommandAsync(toggle, Vector3.UnitZ, attack: false);
        await M4AKit.StepManyAsync(server, 4, a);
        Assert.False(server.Runtime.Player.IsNoclipEnabled);

        // What the integrated single-player server sets (IntegratedLocalServerHandle.StartAsync): the same one-shot now lands.
        server.Host.DevCommandsAllowed = true;
        await a.SendCommandAsync(PlayerMoveIntent.None, Vector3.UnitZ, attack: false);
        await a.SendCommandAsync(toggle, Vector3.UnitZ, attack: false);
        await M4AKit.StepManyAsync(server, 4, a);
        Assert.True(server.Runtime.Player.IsNoclipEnabled);
    }

    [Fact]
    public async Task TheIntegratedLocalServer_EnablesDevCommands_WhileTheDefaultHostDoesNot()
    {
        using MigrationTestDatabase db = new();
        DedicatedServerHost? created = null;
        await using IntegratedLocalServerHandle handle = await IntegratedLocalServerHandle.StartAsync(
            hostFactory: () => created = ServerBootstrap.CreateAuthoritativeHost(
                ServerBootstrap.CreatePersistenceBundle(db.DatabasePath, MigrationTestDatabase.InitScriptPath)));
        Assert.True(handle.IsReady, handle.ErrorMessage);
        Assert.True(created!.DevCommandsAllowed);

        using DedicatedServerHost dedicated = ServerBootstrap.CreateAuthoritativeHost((Dream.Server.Persistence.ServerPersistenceBundle?)null);
        Assert.False(dedicated.DevCommandsAllowed);
    }

    // ---- 4: VisualKey next to stat stripping ----

    [Fact]
    public async Task AnotherPlayersHeldWeaponKey_ReachesAStranger_WhileItsStatsAndExperienceStayHidden()
    {
        using MigrationTestDatabase db = new();
        await using WireLoopbackServer server = await M4AKit.StartAsync(db);
        await using HeadlessPlayScene holder = await M4AKit.JoinAsync(server, SessionA);
        await M4AKit.PlaceAsync(server, holder, M4AKit.Away, holder);
        Assert.True(server.Runtime.OwnerOnlyProgress);

        await using WireLoopbackClient stranger = await server.ConnectAndJoinAsync(SessionB);

        InventorySlotSnapshot blade = holder.Inventory!.Value.Slots.First(slot => slot.Item?.ItemKind == "tool.field-blade");
        ServerActionMessage equipped = await M4AKit.ActAsync(server, holder, new InventoryEquipIntent(blade.SlotIndex), holder);
        Assert.True(equipped.Result.Accepted, equipped.Result.Rejection?.Reason);

        int holderActor = M4AKit.Target(server, SessionA).ActorId;
        List<ActorSnapshot> seen = [];
        for (int i = 0; i < 30; i++)
        {
            await M4AKit.StepAsync(server, holder);
            await server.FlushAllAsync();
            seen.Clear();
            foreach (WireFrame frame in stranger.Frames)
            {
                switch (frame.Decoded.Message)
                {
                    case JoinAcceptedMessage joined:
                        seen.AddRange(joined.EffectiveInitialActors.Where(actor => actor.ActorId == holderActor));
                        break;
                    case ServerSnapshotMessage snapshot:
                        seen.AddRange(snapshot.Snapshot.Actors.Where(actor => actor.ActorId == holderActor));
                        break;
                }
            }

            if (seen.Any(actor => actor.VisualKey is not null))
            {
                break;
            }
        }

        ActorSnapshot withKey = seen.Last(actor => actor.VisualKey is not null);
        PlayerEquipmentVisuals visuals = PlayerEquipmentVisuals.Parse(withKey.VisualKey);
        Assert.NotNull(visuals.MainHand);
        Assert.StartsWith("held.", visuals.MainHand, StringComparison.Ordinal);
        // The stripped fields: no combat stats on any sighting, experience hidden.
        Assert.All(seen, actor =>
        {
            Assert.Null(actor.CombatStats);
            Assert.Equal(WorldSessionRuntime.HiddenExperience, actor.Progression!.Value.Experience);
        });
    }

    // ---- 5: a new character's quickbar and the kit potions ----

    [Fact]
    public async Task ANewCharacter_HoldsKitPotions_QuickbarFiveAndSixResolveToThem_QAndRBothWork()
    {
        using MigrationTestDatabase db = new();
        await using WireLoopbackServer server = await M4AKit.StartAsync(db);
        await using HeadlessPlayScene a = await M4AKit.JoinAsync(server, SessionA);
        await M4AKit.PlaceAsync(server, a, M4AKit.Away, a);
        PlayerInventorySnapshot inventory = a.Inventory!.Value;

        // The kit (v4): the consumable.* healing draughts plus one potion.health and one potion.mana, the kinds keys 5 and 6 are bound to.
        Assert.Single(M4AKit.BagKind(a, "potion.health"));
        Assert.Single(M4AKit.BagKind(a, "potion.mana"));
        Assert.NotEmpty(M4AKit.BagKind(a, "consumable.weak-heal"));
        Assert.NotEmpty(M4AKit.BagKind(a, "consumable.minor-heal"));

        // The bar is the content default, and its keys 5 and 6 are bound to the shop potions by kind.
        Assert.Null(inventory.Quickbar);
        QuickbarHudState bar = a.Quickbar;
        Assert.Equal(new QuickbarSlotSnapshot(4, QuickbarEntryKind.Item, "potion.health"), bar.Slot(4));
        Assert.Equal(new QuickbarSlotSnapshot(5, QuickbarEntryKind.Item, "potion.mana"), bar.Slot(5));
        Assert.True(bar.TryGetItemTarget(4, out _, out _));
        Assert.True(bar.TryGetItemTarget(5, out _, out _));
        Assert.Equal(1, bar.ItemCount(4));
        Assert.Equal(1, bar.ItemCount(5));

        // Q and R look for any potion that restores the pool, whatever its kind.
        Assert.True(CharacterPanelModel.TryFindConsumable(inventory, ConsumablePool.Health, out int anchor, out string kind, out _));
        Assert.StartsWith("consumable.", kind, StringComparison.Ordinal);
        Assert.True(CharacterPanelModel.TryFindConsumable(inventory, ConsumablePool.Mana, out int manaAnchor, out string manaKind, out _));
        Assert.Equal("potion.mana", manaKind);

        // And the server accepts that kit draught.
        PlayerCombatant combatant = M4AKit.Combatant(server, SessionA);
        int max = combatant.Health.MaxHealth;
        combatant.Health.SetAuthoritativeState(max - 60, max);
        Assert.True(CharacterPanelModel.TryGetBagItemId(inventory, anchor, out long itemId));
        ServerActionMessage used = await M4AKit.ActAsync(server, a, new ItemUseIntent(anchor, itemId), a);
        Assert.True(used.Result.Accepted, used.Result.Rejection?.Reason);
        Assert.True(combatant.Health.CurrentHealth > max - 60);

        // R: the kit's mana potion restores mana.
        Assert.True(combatant.TrySpendMana(combatant.MaxMana - 5));
        int manaBefore = combatant.CurrentMana;
        Assert.True(CharacterPanelModel.TryGetBagItemId(inventory, manaAnchor, out long manaItemId));
        ServerActionMessage drank = await M4AKit.ActAsync(server, a, new ItemUseIntent(manaAnchor, manaItemId), a);
        Assert.True(drank.Result.Accepted, drank.Result.Rejection?.Reason);
        Assert.True(combatant.CurrentMana > manaBefore);
    }
}
