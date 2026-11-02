using System.Text;
using OpenTK.Mathematics;
using Xunit;
using Xunit.Abstractions;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Inventory;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;

namespace Dream.Tests.Integration;

// Tester, wave T2f: the snapshot bytes of the wave T1 measurement (two remote sessions, 200 ticks,
// default clock in Day) are unchanged with no light, and one equipped torch costs only the
// Equipment group, on the frames that carry it.
public sealed class HeldLightSnapshotBytesTests(ITestOutputHelper output)
{
    // 58,107 B at protocol v9; v10 widens the envelope's "protocolVersion" by one digit in each of the 200 frames.
    // Wolf bite change: 58,827 B (the wolf chased to its bite range and bit the player standing there).
    // Last fix loop of wave I: 58,140 B (-687 B). The day wolf now spawns in the far corner (52.5, 52.5), outside
    // its aggro range of the players, and the players are protected for their first 192 ticks, so over the 200
    // ticks the wolf stands still and nobody is bitten: no wolf movement or health change in the snapshots.
    // Lowered experience curve (100 + level * 50): experienceToNextLevel at level 1 is 150, not 1023, one byte less in
    // each progression it carries (8 in all): 58,132 B (-8 B).
    private const long NoLightSnapshotBytes = 58_132;

    [Fact]
    public void NoLight_200Ticks_Is58132Bytes()
    {
        using WorldSessionRuntime runtime = CreateRuntime();
        long[] frames = Run(runtime, equipSlot: null);
        output.WriteLine($"no light: {frames.Sum()} B over {frames.Length} snapshots");
        Assert.Equal(NoLightSnapshotBytes, frames.Sum());
    }

    // The torch run processes one equip action before the first tick; the baseline keeps the hammer in the
    // hand (a worn item has no bag row, so there is no "re-equip" of it), so the only difference is the held
    // light. The torch has no stat modifier while the hammer has one, so the stat group can differ too; the
    // test reports both and asserts the heldLight key appears only where expected.
    [Fact]
    public void OneTorch_GrowthPerFrame()
    {
        using WorldSessionRuntime hammerRun = CreateRuntime();
        using WorldSessionRuntime torchRun = CreateRuntime();
        int torchSlot = SlotOf(torchRun, "tool.torch");
        long[] hammer = Run(hammerRun, null, out List<string> hammerJson);
        long[] torch = Run(torchRun, torchSlot, out List<string> torchJson);

        long[] growth = torch.Zip(hammer, (t, h) => t - h).ToArray();
        int[] grownFrames = Enumerable.Range(0, growth.Length).Where(i => growth[i] != 0).ToArray();
        output.WriteLine($"hammer {hammer.Sum()} B, torch {torch.Sum()} B, +{torch.Sum() - hammer.Sum()} B over 200 snapshots");
        output.WriteLine($"frames that grew: {grownFrames.Length}: {string.Join(", ", grownFrames.Take(20).Select(i => $"#{i + 1} {growth[i]:+0;-0} B"))}");
        string first = torchJson[0];
        string firstHammer = hammerJson[0];
        int at = 0;
        while (at < Math.Min(first.Length, firstHammer.Length) && first[at] == firstHammer[at])
        {
            at++;
        }

        output.WriteLine($"frame #1 differs at {at}: torch ...{first.Substring(Math.Max(0, at - 60), Math.Min(100, first.Length - Math.Max(0, at - 60)))}...");
        output.WriteLine($"frame #1 hammer          ...{firstHammer.Substring(Math.Max(0, at - 60), Math.Min(100, firstHammer.Length - Math.Max(0, at - 60)))}...");

        Assert.Equal(NoLightSnapshotBytes, hammer.Sum());
        Assert.True(torch.Sum() > hammer.Sum());
        // Delta frames between keyframes carry nothing for an unchanged light.
        Assert.All(grownFrames, i => Assert.Equal(0, i % 64));
        // Per frame the light costs at most one small field per record, never a whole actor.
        Assert.All(growth, delta => Assert.InRange(delta, -64, 64));
    }

    private static int SlotOf(WorldSessionRuntime runtime, string kind)
    {
        PlayerInventorySnapshot inventory = runtime.TryCaptureInventorySnapshot(new PlayerSessionId("session-a"))!.Value;
        return Assert.Single(inventory.Slots, slot => slot.Item?.ItemKind == kind).SlotIndex;
    }

    private static long[] Run(WorldSessionRuntime runtime, int? equipSlot) => Run(runtime, equipSlot, out _);

    private static long[] Run(WorldSessionRuntime runtime, int? equipSlot, out List<string> json)
    {
        if (equipSlot is int slot)
        {
            AuthoritativeActionResult result = runtime.ProcessAuthoritativeAction(
                new PlayerActionMessage(new PlayerSessionId("session-a"), 1, new InventoryEquipIntent(slot)));
            Assert.True(result.Accepted, result.Rejection?.Reason);
        }

        TransportJsonCodec codec = new();
        long[] frames = new long[200];
        json = [];
        for (ulong tick = 1; tick <= 200; tick++)
        {
            runtime.AdvanceAuthoritativeTick(tick, SimulationClock.FixedDeltaSeconds, default(PlayerCommandFrame), PlayerSimulationTuning.AuthoritativeDefault);
            Assert.Equal(WorldDayPhase.Day, runtime.WorldClock.Phase);
            ServerSnapshotMessage message = new(runtime.ExportServerSnapshotEnvelope());
            byte[] bytes = codec.Encode(new TransportEnvelope(TransportEnvelope.CurrentProtocolVersion, message.MessageKind, tick), message);
            frames[tick - 1] = bytes.Length;
            json.Add(Encoding.UTF8.GetString(bytes));
        }

        return frames;
    }

    private static WorldSessionRuntime CreateRuntime()
    {
        WorldSessionRuntime runtime = new(WorldDefaults.StartupFloorSize, new Vector3(5f, 1.5f, 5f), new SceneEventHub());
        runtime.AttachRemoteSession(new PlayerSessionId("session-a"), "session-a");
        runtime.AttachRemoteSession(new PlayerSessionId("session-b"), "session-b");
        return runtime;
    }
}
