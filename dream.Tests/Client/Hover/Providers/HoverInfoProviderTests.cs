using Dream.Client.Hover;
using Dream.Client.Hover.Providers;
using Dream.Client.Presentation;
using Dream.Client.Replication;
using Dream.Multiplayer.Protocol;
using Dream.Rendering.Actors;
using Dream.Rendering.Ui;
using Dream.Simulation.Combat;
using Dream.Simulation.Enemies;
using Dream.Simulation.Inventory;
using Dream.Simulation.World;
using Dream.Tests.Utilities;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Client.Hover.Providers;

public sealed class HoverInfoProviderTests
{
    private const string Torch = "tool.torch";

    private static EnemyActor AddEnemy(EnemyRegistry registry, int id, int hp = 80, int max = 80, int level = 3,
        string name = "Wolf", EnemyBehaviorState state = EnemyBehaviorState.Idle)
    {
        Vector3 position = new(2f, 0f, 2f);
        EnemyCombatant combatant = new(() => position, actorId: id, maxHealth: Math.Max(1, max));
        combatant.Health.SetAuthoritativeState(hp, max);
        EnemyActor enemy = new(combatant, position, aggroRange: 5f, attackRange: 1.25f, moveSpeed: 3f, displayName: name, level: level);
        enemy.SetAuthoritativeState(position, -Vector3.UnitZ, state);
        registry.Add(enemy);
        return enemy;
    }

    private static HoverTargetId Enemy(int id) => new(HoverKind.Enemy, id);

    [Fact]
    public void EnemyFillsEveryField()
    {
        EnemyRegistry registry = new();
        AddEnemy(registry, 7, hp: 30, max: 80, level: 4, name: "Grey Wolf", state: EnemyBehaviorState.Chasing);
        HoverInfo info = new();

        Assert.True(new EnemyHoverInfoProvider(registry).TryDescribe(Enemy(7), info));

        Assert.Equal(Enemy(7), info.Target);
        Assert.Equal(HoverDisposition.Hostile, info.Disposition);
        Assert.Equal("Grey Wolf", info.Title);
        Assert.Equal("Lv 4", info.Subtitle);
        Assert.Equal(1, info.BarCount);
        Assert.Equal(new HoverBar(HoverBarKind.Health, 30, 80, "30 / 80"), info.Bars[0]);
        Assert.Equal(1, info.LineCount);
        Assert.Equal("Hunting", info.Lines[0].Value);
        Assert.Equal(EnemyHoverInfoProvider.AttackHint, info.Hint);
        Assert.False(info.Defeated);
    }

    [Theory]
    [InlineData(EnemyBehaviorState.Idle, "Idle")]
    [InlineData(EnemyBehaviorState.Chasing, "Hunting")]
    [InlineData(EnemyBehaviorState.Attacking, "Attacking")]
    public void EnemyStateWording(EnemyBehaviorState state, string expected)
    {
        EnemyRegistry registry = new();
        AddEnemy(registry, 1, state: state);
        HoverInfo info = new();
        new EnemyHoverInfoProvider(registry).TryDescribe(Enemy(1), info);
        Assert.Equal(expected, info.Lines[0].Value);
    }

    [Fact]
    public void DeadOrUnknownEnemyIsGoneAndInfoIsUntouched()
    {
        EnemyRegistry registry = new();
        EnemyActor dead = AddEnemy(registry, 1, hp: 80);
        dead.Combatant.Health.SetAuthoritativeState(0, 80);
        HoverInfo info = new() { Title = "keep" };
        EnemyHoverInfoProvider provider = new(registry);

        Assert.False(provider.TryDescribe(Enemy(1), info));
        Assert.False(provider.TryDescribe(Enemy(99), info));
        Assert.False(provider.TryDescribe(new HoverTargetId(HoverKind.Enemy, long.MaxValue), info));
        Assert.Equal("keep", info.Title);
    }

    [Fact]
    public void EnemyHostileWireValuesAreClamped()
    {
        EnemyRegistry registry = new();
        AddEnemy(registry, 1, hp: -50, max: 0, level: int.MaxValue, name: new string('x', 4096));
        AddEnemy(registry, 2, hp: 500, max: 100, level: -5);
        HoverInfo info = new();
        EnemyHoverInfoProvider provider = new(registry);

        Assert.True(provider.TryDescribe(Enemy(1), info) || !registry.ActiveActors[0].IsActive);
        registry.ActiveActors[0].Combatant.Health.SetAuthoritativeState(1, 0);
        // Whatever the health state did with the hostile values, a bar is never negative and never above max.
        if (provider.TryDescribe(Enemy(1), info))
        {
            Assert.InRange(info.Bars[0].Current, 0, Math.Max(0, info.Bars[0].Max));
            Assert.Equal($"Lv {ClientScenePresenter.MaxDisplayLevel}", info.Subtitle);
        }

        Assert.True(provider.TryDescribe(Enemy(2), info));
        Assert.InRange(info.Bars[0].Current, 0, info.Bars[0].Max);
        Assert.Equal("Lv 1", info.Subtitle);
    }

    [Fact]
    public void EnemyLevelClampsAtTheDisplayCeiling()
    {
        EnemyRegistry registry = new();
        AddEnemy(registry, 1, level: int.MaxValue);
        HoverInfo info = new();
        Assert.True(new EnemyHoverInfoProvider(registry).TryDescribe(Enemy(1), info));
        Assert.Equal($"Lv {ClientScenePresenter.MaxDisplayLevel}", info.Subtitle);
    }

    [Fact]
    public void NpcTalkHintFollowsRange()
    {
        ClientNpcCache npcs = new();
        npcs.ApplyActors([new ActorSnapshot(5, CreatureKind.Npc, null, new Vector3(10f, 0f, 0f), -Vector3.UnitZ, 1, 1, true, DisplayName: "Mara")]);
        NpcHoverInfoProvider provider = new(npcs) { PlayerPosition = new Vector3(10f + ClientNpcCache.InteractRange - 0.5f, 3f, 0f) };
        HoverInfo info = new();
        HoverTargetId id = new(HoverKind.Npc, 5);

        Assert.True(provider.TryDescribe(id, info));
        Assert.Equal("Mara", info.Title);
        Assert.Equal(HoverDisposition.Friendly, info.Disposition);
        Assert.Equal(0, info.BarCount);
        Assert.Equal(WorldEntityRenderer.TalkHint, info.Hint);

        provider.PlayerPosition = new Vector3(10f + ClientNpcCache.InteractRange + 0.5f, 0f, 0f);
        Assert.True(provider.TryDescribe(id, info));
        Assert.Equal(string.Empty, info.Hint);

        Assert.False(provider.TryDescribe(new HoverTargetId(HoverKind.Npc, 6), info));
    }

    private static ClientGroundItemCache Ground(params GroundItemSnapshot[] items)
    {
        ClientGroundItemCache cache = new();
        cache.Apply(new ServerGroundItems(true, items, []));
        return cache;
    }

    [Theory]
    [InlineData(ItemRarity.Common, ItemTier.Common, "Common")]
    [InlineData(ItemRarity.Uncommon, ItemTier.Uncommon, "Uncommon")]
    [InlineData(ItemRarity.Rare, ItemTier.Rare, "Rare")]
    public void GroundItemTierAndRarity(ItemRarity rarity, ItemTier tier, string word)
    {
        ClientGroundItemCache ground = Ground(new GroundItemSnapshot(9, Torch, rarity, new Vector3(1f, 0f, 0f), 0, 0));
        HoverInfo info = new();
        Assert.True(new GroundItemHoverInfoProvider(ground).TryDescribe(new HoverTargetId(HoverKind.GroundItem, 9), info));
        Assert.Equal(HoverDisposition.Item, info.Disposition);
        Assert.Equal(tier, info.Tier);
        Assert.Equal(word, info.Subtitle);
        Assert.True(ground.TryGet(9, out ClientGroundItem item));
        Assert.Equal(item.DisplayName, info.Title);
    }

    [Fact]
    public void GroundItemHintPickUpClaimedOrNone()
    {
        ClientGroundItemCache ground = Ground(
            new GroundItemSnapshot(1, Torch, ItemRarity.Common, new Vector3(1f, 0f, 0f), 0, 0),
            new GroundItemSnapshot(2, Torch, ItemRarity.Common, new Vector3(1f, 0f, 0f), 77, 100),
            new GroundItemSnapshot(3, Torch, ItemRarity.Common, new Vector3(50f, 0f, 0f), 0, 0));
        GroundItemHoverInfoProvider provider = new(ground) { LocalActorId = 5, ServerTick = 10 };
        HoverInfo info = new();

        provider.TryDescribe(new HoverTargetId(HoverKind.GroundItem, 1), info);
        Assert.Equal(WorldEntityRenderer.PickUpHint, info.Hint);
        provider.TryDescribe(new HoverTargetId(HoverKind.GroundItem, 2), info);
        Assert.Equal(WorldEntityRenderer.ClaimedHint, info.Hint);
        provider.ServerTick = 100;
        provider.TryDescribe(new HoverTargetId(HoverKind.GroundItem, 2), info);
        Assert.Equal(WorldEntityRenderer.PickUpHint, info.Hint);
        provider.LocalActorId = 77;
        provider.ServerTick = 10;
        provider.TryDescribe(new HoverTargetId(HoverKind.GroundItem, 2), info);
        Assert.Equal(WorldEntityRenderer.PickUpHint, info.Hint);
        provider.TryDescribe(new HoverTargetId(HoverKind.GroundItem, 3), info);
        Assert.Equal(string.Empty, info.Hint);
        Assert.False(provider.TryDescribe(new HoverTargetId(HoverKind.GroundItem, 404), info));
    }

    private static RemotePlayerPresentationState Remote(int id, int hp, int max, int level, bool alive = true, string name = "Ann") =>
        new(id, "s" + id, name, default, hp, max, alive, Level: level);

    [Fact]
    public void RemotePlayerFillsNameLevelAndBar()
    {
        RemotePlayerHoverInfoProvider provider = new() { Players = [Remote(3, 40, 100, 12)] };
        HoverInfo info = new();
        HoverTargetId id = new(HoverKind.RemotePlayer, 3);

        Assert.True(provider.TryDescribe(id, info));
        Assert.Equal(HoverDisposition.Friendly, info.Disposition);
        Assert.Equal("Ann", info.Title);
        Assert.Equal("Lv 12", info.Subtitle);
        Assert.Equal(new HoverBar(HoverBarKind.Health, 40, 100, "40 / 100"), info.Bars[0]);

        provider.Players = [Remote(3, 0, 100, 12, alive: false)];
        Assert.False(provider.TryDescribe(id, info));
        provider.Players = [];
        Assert.False(provider.TryDescribe(id, info));
    }

    [Fact]
    public void RemotePlayerHostileValues()
    {
        RemotePlayerHoverInfoProvider provider = new() { Players = [Remote(3, -9, 0, int.MaxValue, name: new string('y', 4096))] };
        HoverInfo info = new();
        Assert.True(provider.TryDescribe(new HoverTargetId(HoverKind.RemotePlayer, 3), info));
        Assert.Equal(new HoverBar(HoverBarKind.Health, 0, 0, "0 / 0"), info.Bars[0]);
        Assert.Equal($"Lv {ClientScenePresenter.MaxDisplayLevel}", info.Subtitle);
    }

    [Fact]
    public void ComposerDispatchesByKindAndTreatsMissingProviderAsGone()
    {
        EnemyRegistry registry = new();
        AddEnemy(registry, 1);
        HoverInfoComposer composer = new([new EnemyHoverInfoProvider(registry)]);

        Assert.True(composer.Compose(Enemy(1)));
        Assert.Equal(Enemy(1), composer.Info.Target);
        Assert.False(composer.Compose(new HoverTargetId(HoverKind.Npc, 1)));
        Assert.True(composer.Info.Target.IsNone);
        Assert.False(composer.Compose(new HoverTargetId(HoverKind.Interactable, 1)));
    }

    [Fact]
    public void ComposerNoneClears()
    {
        EnemyRegistry registry = new();
        AddEnemy(registry, 1);
        HoverInfoComposer composer = new([new EnemyHoverInfoProvider(registry)]);
        composer.Compose(Enemy(1));

        Assert.False(composer.Compose(HoverTargetId.None));
        Assert.True(composer.Info.Target.IsNone);
        Assert.Equal(string.Empty, composer.Info.Title);
        Assert.Equal(0, composer.Info.BarCount);
    }

    [Fact]
    public void ComposerKeepsAGoneTargetAsDefeated()
    {
        EnemyRegistry registry = new();
        EnemyActor enemy = AddEnemy(registry, 1, hp: 50, max: 80, name: "Grey Wolf");
        HoverInfoComposer composer = new([new EnemyHoverInfoProvider(registry)]);
        composer.Compose(Enemy(1));

        enemy.Combatant.Health.SetAuthoritativeState(0, 80);
        Assert.False(composer.Compose(Enemy(1)));
        Assert.True(composer.Info.Defeated);
        Assert.Equal("Grey Wolf", composer.Info.Title);
        Assert.Equal(Enemy(1), composer.Info.Target);
        Assert.Equal(new HoverBar(HoverBarKind.Health, 0, 80, "0 / 80"), composer.Info.Bars[0]);

        // Still gone next frame: stays defeated.
        Assert.False(composer.Compose(Enemy(1)));
        Assert.True(composer.Info.Defeated);

        // A different gone target clears.
        Assert.False(composer.Compose(Enemy(2)));
        Assert.True(composer.Info.Target.IsNone);
        Assert.False(composer.Info.Defeated);
    }

    [Fact]
    public void ComposerBindsLiveHealth()
    {
        EnemyRegistry registry = new();
        EnemyActor enemy = AddEnemy(registry, 1, hp: 80, max: 80);
        HoverInfoComposer composer = new([new EnemyHoverInfoProvider(registry)]);

        composer.Compose(Enemy(1));
        Assert.Equal("80 / 80", composer.Info.Bars[0].Text);

        enemy.Combatant.Health.SetAuthoritativeState(35, 80);
        composer.Compose(Enemy(1));
        Assert.Equal(35, composer.Info.Bars[0].Current);
        Assert.Equal("35 / 80", composer.Info.Bars[0].Text);
    }

    [Fact]
    public void ComposeAllocatesNothingWhileValuesHold()
    {
        EnemyRegistry registry = new();
        AddEnemy(registry, 1);
        ClientNpcCache npcs = new();
        npcs.ApplyActors([new ActorSnapshot(5, CreatureKind.Npc, null, Vector3.Zero, -Vector3.UnitZ, 1, 1, true, DisplayName: "Mara")]);
        ClientGroundItemCache ground = Ground(new GroundItemSnapshot(9, Torch, ItemRarity.Rare, new Vector3(1f, 0f, 0f), 0, 0));
        HoverInfoComposer composer = new([
            new EnemyHoverInfoProvider(registry),
            new NpcHoverInfoProvider(npcs),
            new GroundItemHoverInfoProvider(ground),
            new RemotePlayerHoverInfoProvider { Players = [Remote(3, 40, 100, 12)] }]);

        foreach (HoverTargetId id in new[]
                 {
                     Enemy(1), new HoverTargetId(HoverKind.Npc, 5), new HoverTargetId(HoverKind.GroundItem, 9),
                     new HoverTargetId(HoverKind.RemotePlayer, 3)
                 })
        {
            HoverTargetId target = id;
            Assert.True(composer.Compose(target));
            AllocationAssert.AtMost(0, () =>
            {
                for (int n = 0; n < 100; n++)
                {
                    composer.Compose(target);
                }
            });
        }
    }

    [Fact]
    public void TextCacheIsBounded()
    {
        HoverTextCache cache = new();
        for (int n = 0; n < HoverTextCache.MaxEntries * 3; n++)
        {
            cache.Bar(n, 100);
            cache.Level(n + 1);
        }

        Assert.True(cache.Count <= HoverTextCache.MaxEntries * 2);
        Assert.Equal("5 / 9", cache.Bar(5, 9));
    }
}
