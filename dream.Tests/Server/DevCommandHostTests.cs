using System.Reflection;
using Xunit;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Server;
using Dream.Simulation.Enemies;
using Dream.Simulation.Inventory;
using Dream.Simulation.Runtime;

namespace Dream.Tests.Server;

// The /dev line on a runtime-attached host: the gate (dev commands on AND a loopback connection), the routing
// away from the admin path, and each verb's effect on the world. Every gate has a negative that must leave the
// world as it was.
public sealed class DevCommandHostTests
{
    private const string Loopback = "127.0.0.1:50001";
    private const string Remote = "203.0.113.5:4000";
    private const string Session = "session-a";

    [Theory]
    [InlineData(true, Loopback, true)]
    [InlineData(true, "[::1]:50001", true)]
    [InlineData(false, Loopback, false)]
    [InlineData(true, Remote, false)]
    [InlineData(false, Remote, false)]
    [InlineData(true, "not-an-endpoint", false)]
    public void TheGate_IsDevCommandsAllowedAndLoopback(bool allowed, string connection, bool applied)
    {
        using Fixture f = new(allowed, connection);
        f.SetHealth(1);

        List<string> replies = f.Dev("heal");

        string reply = Assert.Single(replies);
        if (applied)
        {
            Assert.StartsWith(DevCommandText.ReplyPrefix, reply, StringComparison.Ordinal);
            Assert.Equal(f.Target.Combatant.Health.MaxHealth, f.Target.Combatant.Health.CurrentHealth);
        }
        else
        {
            Assert.Equal(DedicatedServerHost.DevCommandsOffText, reply);
            Assert.Equal("[dev] error: Dev commands are off on this server.", reply);
            Assert.Equal(1, f.Target.Combatant.Health.CurrentHealth);
        }
    }

    [Fact]
    public void WhenOff_NothingRuns_NotEvenASpawn_AndTheGrammarIsNotRevealed()
    {
        using Fixture f = new(allowed: false, Loopback);
        int before = f.EnemyCount();

        List<string> replies = f.Dev("spawn wolf 4; nonsense; give tool.torch");

        Assert.Equal([DedicatedServerHost.DevCommandsOffText], replies);
        Assert.Equal(before, f.EnemyCount());
    }

    [Fact]
    public void ARemotePlayer_OnAPublicHost_CannotUseItEvenWithDevCommandsOn()
    {
        using Fixture f = new(allowed: true, Remote);
        int before = f.EnemyCount();

        Assert.Equal([DedicatedServerHost.DevCommandsOffText], f.Dev("spawn wolf 3"));
        Assert.Equal(before, f.EnemyCount());
    }

    [Fact]
    public void ADevLine_NeverReachesTheAdminPath_AndAnAdminLineStaysDenied()
    {
        using Fixture f = new(allowed: true, Loopback);

        // A player without the admin role is still refused an admin command, dev commands on or not ...
        Assert.Equal([DedicatedServerHost.PermissionDeniedText], f.Say(ChatChannel.Command, "/kick Bob"));

        // ... and a dev line is not an admin command: it is answered by the dev path, "[dev] ", never the
        // permission text, and the admin role plays no part in it.
        string reply = Assert.Single(f.Dev("heal"));
        Assert.StartsWith(DevCommandText.ReplyPrefix, reply, StringComparison.Ordinal);

        using Fixture off = new(allowed: false, Loopback);
        Assert.Equal([DedicatedServerHost.PermissionDeniedText], off.Say(ChatChannel.Command, "/ban Bob"));
        Assert.Equal([DedicatedServerHost.DevCommandsOffText], off.Dev("heal"));
    }

    [Fact]
    public void DevAlone_AnswersOneHelpLine()
    {
        using Fixture f = new(allowed: true, Loopback);

        string reply = Assert.Single(f.Say(ChatChannel.Command, "/dev"));

        Assert.StartsWith(DevCommandText.ReplyPrefix, reply, StringComparison.Ordinal);
        Assert.Contains("spawn", reply, StringComparison.Ordinal);
    }

    [Fact]
    public void MoreThanEightSubcommands_IsOneError_AndNothingRuns()
    {
        using Fixture f = new(allowed: true, Loopback);
        f.SetHealth(1);

        List<string> replies = f.Dev(string.Join("; ", Enumerable.Repeat("heal", DevCommandText.MaxSubcommands + 1)));

        string reply = Assert.Single(replies);
        Assert.StartsWith(DevCommandText.ErrorPrefix, reply, StringComparison.Ordinal);
        Assert.Equal(1, f.Target.Combatant.Health.CurrentHealth);

        // Control: exactly the limit runs, one reply each.
        Assert.Equal(DevCommandText.MaxSubcommands, f.Dev(string.Join("; ", Enumerable.Repeat("heal", DevCommandText.MaxSubcommands))).Count);
    }

    [Fact]
    public void Subcommands_RunInOrder_AFailureRepliesAndTheNextStillRuns()
    {
        using Fixture f = new(allowed: true, Loopback);

        List<string> replies = f.Dev("hp 7; frobnicate; spawn NoSuchBeast; mana 3");

        Assert.Equal(4, replies.Count);
        Assert.StartsWith(DevCommandText.ReplyPrefix, replies[0], StringComparison.Ordinal);
        Assert.StartsWith(DevCommandText.ErrorPrefix, replies[1], StringComparison.Ordinal);
        Assert.StartsWith(DevCommandText.ErrorPrefix, replies[2], StringComparison.Ordinal);
        Assert.StartsWith(DevCommandText.ReplyPrefix, replies[3], StringComparison.Ordinal);
        Assert.Equal(7, f.Target.Combatant.Health.CurrentHealth);
        Assert.Equal(3, f.Target.Combatant.CurrentMana);
    }

    [Fact]
    public void Spawn_PlacesTheCountOnARingAroundTheSender_WithNoCampKey()
    {
        using Fixture f = new(allowed: true, Loopback);
        f.Dev("clear");
        Assert.Equal(0, f.EnemyCount());

        string reply = Assert.Single(f.Dev("spawn WOLF 3 3"));

        Assert.StartsWith("[dev] spawned 3 Wolf", reply, StringComparison.Ordinal);
        List<EnemyActor> spawned = f.Enemies();
        Assert.Equal(3, spawned.Count);
        foreach (EnemyActor enemy in spawned)
        {
            float dx = enemy.Position.X - f.Target.Position.X;
            float dz = enemy.Position.Z - f.Target.Position.Z;
            Assert.InRange(MathF.Sqrt((dx * dx) + (dz * dz)), 2.9f, 3.1f);
            Assert.False(f.HasCampKey(enemy.Combatant.Actor.Id), "a dev-spawned enemy belongs to no camp");
        }
    }

    [Fact]
    public void Spawn_DefaultsToOneAtFourMetres()
    {
        using Fixture f = new(allowed: true, Loopback);
        f.Dev("clear");

        f.Dev("spawn wolf");

        EnemyActor enemy = Assert.Single(f.Enemies());
        float dx = enemy.Position.X - f.Target.Position.X;
        float dz = enemy.Position.Z - f.Target.Position.Z;
        Assert.InRange(MathF.Sqrt((dx * dx) + (dz * dz)), 3.9f, 4.1f);
    }

    [Fact]
    public void Clear_RemovesEveryEnemy_OrOnlyThoseWithinTheRadius()
    {
        using Fixture f = new(allowed: true, Loopback);
        f.Dev("clear");
        f.Dev("spawn wolf 2 2");
        f.Dev("spawn wolf 1 8");
        Assert.Equal(3, f.EnemyCount());

        string near = Assert.Single(f.Dev("clear 4"));

        Assert.StartsWith("[dev] cleared 2", near, StringComparison.Ordinal);
        Assert.Equal(1, f.EnemyCount());

        string all = Assert.Single(f.Dev("clear"));
        Assert.StartsWith("[dev] cleared 1", all, StringComparison.Ordinal);
        Assert.Equal(0, f.EnemyCount());
    }

    [Fact]
    public void Teleport_ToCoordinates_PutsTheSenderOnTheSurfaceThere()
    {
        using Fixture f = new(allowed: true, Loopback);

        string reply = Assert.Single(f.Dev("tp 20.5 18.25"));

        Assert.StartsWith(DevCommandText.ReplyPrefix, reply, StringComparison.Ordinal);
        Assert.Equal(20.5f, f.Target.Position.X, 2);
        Assert.Equal(18.25f, f.Target.Position.Z, 2);
        Assert.True(f.Target.Position.Y >= 1f);
    }

    [Fact]
    public void Teleport_OutsideTheMap_IsAnError_AndTheSenderStaysPut()
    {
        using Fixture f = new(allowed: true, Loopback);
        Vector3Snapshot before = new(f.Target.Position);

        List<string> replies = f.Dev("tp 5000 5; tp -3 4");

        Assert.All(replies, r => Assert.StartsWith(DevCommandText.ErrorPrefix, r, StringComparison.Ordinal));
        Assert.Equal(before, new Vector3Snapshot(f.Target.Position));
    }

    [Fact]
    public void Teleport_ToSpawn_Camp_AndNpc()
    {
        using Fixture f = new(allowed: true, Loopback);

        // The test assembly's default content has no camps or vendors; this test teleports to the shipped ones.
        f.Runtime.EnemyContent = Dream.Simulation.Content.ContentCatalog.Shipped;
        f.Dev("tp 20 20");

        Assert.StartsWith(DevCommandText.ReplyPrefix, Assert.Single(f.Dev("tp spawn")), StringComparison.Ordinal);
        Assert.Equal(5f, f.Target.Position.X, 1);
        Assert.Equal(5f, f.Target.Position.Z, 1);

        SpawnCampKey camp = SpawnCampKey.First(f.Runtime);
        f.Dev("tp camp " + camp.Key.ToUpperInvariant());
        Assert.NotEqual(5f, f.Target.Position.X);

        Assert.StartsWith(DevCommandText.ErrorPrefix, Assert.Single(f.Dev("tp camp no-such-camp")), StringComparison.Ordinal);

        f.Runtime.SpawnContentNpcs();
        Assert.NotEmpty(f.Runtime.Npcs);
        string npcKey = f.Runtime.Npcs[0].Definition.Key;
        f.Dev("tp spawn");
        Assert.StartsWith(DevCommandText.ReplyPrefix, Assert.Single(f.Dev("tp npc " + npcKey.ToUpperInvariant())), StringComparison.Ordinal);
        Assert.StartsWith(DevCommandText.ErrorPrefix, Assert.Single(f.Dev("tp npc nobody")), StringComparison.Ordinal);
    }

    [Fact]
    public void Give_RolledItem_LandsAtTheRequestedRarity()
    {
        using Fixture f = new(allowed: true, Loopback);

        string reply = Assert.Single(f.Dev("give GEAR.IRON-DAGGER rare 2"));

        Assert.StartsWith("[dev] gave 2 gear.iron-dagger", reply, StringComparison.Ordinal);
        List<InventoryItemSnapshot> held = f.Items("gear.iron-dagger");
        Assert.Equal(2, held.Count);
        Assert.All(held, item =>
        {
            Assert.Equal(ItemRarity.Rare, item.Rarity);
            Assert.True(item.IsRolled);
        });
    }

    [Fact]
    public void Give_FixedItem_LandsWithoutChangingTheWallet()
    {
        using Fixture f = new(allowed: true, Loopback);
        long gold = f.Runtime.InventoryService.GoldOf(f.CharacterKey);
        int torches = f.Items("tool.torch").Count;

        string reply = Assert.Single(f.Dev("give tool.torch rare 2"));

        Assert.StartsWith("[dev] gave 2 tool.torch", reply, StringComparison.Ordinal);
        Assert.Equal(torches + 2, f.Items("tool.torch").Count);
        Assert.Equal(gold, f.Runtime.InventoryService.GoldOf(f.CharacterKey));
    }

    [Fact]
    public void Give_UnknownItem_IsAnError_AndAFullBagStopsTheGrantAndSaysHowManyLanded()
    {
        using Fixture f = new(allowed: true, Loopback);

        Assert.StartsWith(DevCommandText.ErrorPrefix, Assert.Single(f.Dev("give no.such.item")), StringComparison.Ordinal);

        string reply = Assert.Single(f.Dev("give gear.tower-shield 20"));
        Assert.Contains("bag is full", reply, StringComparison.Ordinal);
        Assert.StartsWith(DevCommandText.ReplyPrefix, reply, StringComparison.Ordinal);
        int landed = f.Items("gear.tower-shield").Count;
        Assert.InRange(landed, 1, 19);
        Assert.Contains($"gave {landed} of 20", reply, StringComparison.Ordinal);
    }

    [Fact]
    public void HealAndVitals_SetCurrentValues_ClampedToTheirRange()
    {
        using Fixture f = new(allowed: true, Loopback);
        int max = f.Target.Combatant.Health.MaxHealth;
        int maxMana = f.Target.Combatant.MaxMana;

        f.Dev("hp 0");
        Assert.Equal(1, f.Target.Combatant.Health.CurrentHealth);
        f.Dev("hp 100000000");
        Assert.Equal(max, f.Target.Combatant.Health.CurrentHealth);
        f.Dev("hp -4");
        Assert.Equal(1, f.Target.Combatant.Health.CurrentHealth);

        f.Dev("mana 0");
        Assert.Equal(0, f.Target.Combatant.CurrentMana);
        f.Dev("mana 100000000");
        Assert.Equal(maxMana, f.Target.Combatant.CurrentMana);

        f.Dev("hp 3; mana 2");
        f.Dev("heal");
        Assert.Equal(max, f.Target.Combatant.Health.CurrentHealth);
        Assert.Equal(maxMana, f.Target.Combatant.CurrentMana);
    }

    [Fact]
    public void ADeadSender_IsRefused_AndNothingIsStaged()
    {
        using Fixture f = new(allowed: true, Loopback);
        int max = f.Target.Combatant.Health.MaxHealth;
        f.Target.Combatant.Health.SetAuthoritativeState(0, max);
        Assert.False(f.Target.IsAlive);

        List<string> replies = f.Dev("heal; spawn wolf");

        Assert.All(replies, r => Assert.StartsWith(DevCommandText.ErrorPrefix, r, StringComparison.Ordinal));
        Assert.Equal(0, f.Target.Combatant.Health.CurrentHealth);
    }

    private readonly record struct Vector3Snapshot(float X, float Y, float Z)
    {
        public Vector3Snapshot(OpenTK.Mathematics.Vector3 v)
            : this(v.X, v.Y, v.Z)
        {
        }
    }

    private readonly record struct SpawnCampKey(string Key)
    {
        public static SpawnCampKey First(WorldSessionRuntime runtime)
        {
            Assert.NotEmpty(runtime.EnemyContent.Camps);
            return new SpawnCampKey(runtime.EnemyContent.Camps[0].Key);
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly DedicatedServerHost _host;
        private readonly string _connection;
        private readonly ManualClock _clock = new();

        public Fixture(bool allowed, string connection)
        {
            _connection = connection;
            ProtocolRouter router = new(
                [
                    new ClientHelloHandler(),
                    new JoinRequestHandler(),
                    new PlayerCommandHandler(),
                    new PlayerActionHandler(),
                    new WorldResyncRequestHandler(),
                    new ChatMessageHandler()
                ],
                Array.Empty<IClientMessageHandler>());
            _host = new DedicatedServerHost(RuntimeAuthoritativeSessionHost.CreateDefault(), router)
            {
                TimeProvider = _clock,
                DevCommandsAllowed = allowed
            };
            Assert.IsType<JoinAcceptedMessage>(Assert.Single(
                _host.HandleClientMessage(connection, new ClientJoinRequestMessage(Session, "Alice"))));
            Assert.True(Runtime.TryGetSessionPlayerTarget(new PlayerSessionId(Session), out IAuthoritativePlayerTarget? target));
            Target = target!;
            Assert.True(Runtime.TryGetCharacterKey(new PlayerSessionId(Session), out string key));
            CharacterKey = key;
        }

        public WorldSessionRuntime Runtime => _host.Runtime!;

        public IAuthoritativePlayerTarget Target { get; }

        public string CharacterKey { get; }

        public List<string> Dev(string subcommands) => Say(ChatChannel.Command, DevCommandText.Command + " " + subcommands);

        // Sends one line from the player and returns the text of the System replies it got back.
        public List<string> Say(ChatChannel channel, string text)
        {
            _clock.Advance(TimeSpan.FromSeconds(2));
            _host.HandleClientMessage(_connection, new ClientChatMessage(channel, text));
            List<PendingConnectionReply> replies = [];
            _host.DrainPendingJoinRepliesInto(replies);
            return
            [
                .. replies
                    .Where(r => r.ConnectionId == _connection)
                    .Select(r => r.Message)
                    .OfType<ServerChatMessage>()
                    .Where(m => m.Channel == ChatChannel.System)
                    .Select(m => m.Text)
            ];
        }

        public void SetHealth(int value) => Target.Combatant.Health.SetAuthoritativeState(value, Target.Combatant.Health.MaxHealth);

        public List<EnemyActor> Enemies() => [.. Runtime.Enemies.ActiveActors.Where(e => e.IsActive)];

        public int EnemyCount() => Enemies().Count;

        public List<InventoryItemSnapshot> Items(string kind)
            => [.. Runtime.InventoryService.BuildSnapshot(CharacterKey).Slots
                .Select(s => s.Item)
                .Where(i => i != null && i.Value.ItemKind == kind)
                .Select(i => i!.Value)];

        public bool HasCampKey(int actorId)
        {
            FieldInfo field = typeof(WorldSessionRuntime).GetField("_persistence", BindingFlags.Instance | BindingFlags.NonPublic)!;
            object bridge = field.GetValue(Runtime)!;
            MethodInfo method = bridge.GetType().GetMethod("TryGetEnemyCampKey")!;
            object?[] args = [actorId, null];
            return (bool)method.Invoke(bridge, args)!;
        }

        public void Dispose() => _host.Dispose();
    }

    private sealed class ManualClock : TimeProvider
    {
        private long _timestamp = 1_000_000;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _timestamp;

        public void Advance(TimeSpan by) => _timestamp += by.Ticks;
    }
}
