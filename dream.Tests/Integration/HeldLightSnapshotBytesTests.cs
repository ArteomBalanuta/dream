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
// default clock in Day) are pinned, and a held light costs only the Equipment group, on the frames
// that carry it. The starter kit wears its torch in the off hand now (823f6eb), so there is no bag row
// to equip from: the growth test runs the kit as it ships against a control that takes the torch off.
public sealed class HeldLightSnapshotBytesTests(ITestOutputHelper output)
{
    // 58,107 B at protocol v9; v10 widens the envelope's "protocolVersion" by one digit in each of the 200 frames.
    // Wolf bite change: 58,827 B (the wolf chased to its bite range and bit the player standing there).
    // Last fix loop of wave I: 58,140 B (-687 B). The day wolf now spawns in the far corner (52.5, 52.5), outside
    // its aggro range of the players, and the players are protected for their first 192 ticks, so over the 200
    // ticks the wolf stands still and nobody is bitten: no wolf movement or health change in the snapshots.
    // Lowered experience curve (100 + level * 50): experienceToNextLevel at level 1 is 150, not 1023, one byte less in
    // each progression it carries (8 in all): 58,132 B (-8 B).
    // Character stats sheet (3fc2a4f): the Combat wire group carries Agility and Energy as well, 6 B more per player
    // on each of the four keyframes: 58,180 B (+48 B). The starter kit's worn torch (823f6eb) is not in this total -
    // the snapshot's held light and held visual come from the inventory's equipped-summary cache, and this run never
    // reads an inventory, so no actor in it carries either. Kit.WornTorch below is the run that does.
    private const long TwoSessionSnapshotBytes = 58_180;

    // What a run does with the two sessions' equipment before its 200 ticks.
    private enum Kit
    {
        // Nothing at all: the wave T1 measurement, and the configuration the byte total is pinned to.
        Untouched,

        // The kit as it ships, read once so the equipped summary is live: the builder hammer in the main
        // hand and the lit torch in the off hand.
        WornTorch,

        // The same kit with the torch taken out of the off hand: the no-light control. A worn item has no
        // bag row, so unequipping is the only way to reach an unlit actor now.
        NoTorch,
    }

    [Fact]
    public void TwoSessions_200Ticks_Is58180Bytes()
    {
        using WorldSessionRuntime runtime = CreateRuntime();
        long[] frames = Run(runtime, Kit.Untouched);
        output.WriteLine($"two sessions: {frames.Sum()} B over {frames.Length} snapshots");
        Assert.Equal(TwoSessionSnapshotBytes, frames.Sum());
    }

    // Both runs load the same shipped kit, with the hammer in the main hand; the control differs only in
    // that the lit torch leaves the off hand, which is what the held light follows now. The torch has no
    // stat modifier, so the stat group does not move with it; the test reports both runs and asserts the
    // growth appears only where expected.
    [Fact]
    public void OneTorch_GrowthPerFrame()
    {
        using WorldSessionRuntime noLightRun = CreateRuntime();
        using WorldSessionRuntime torchRun = CreateRuntime();
        long[] noLight = Run(noLightRun, Kit.NoTorch, out List<string> noLightJson);
        long[] torch = Run(torchRun, Kit.WornTorch, out List<string> torchJson);

        long[] growth = torch.Zip(noLight, (t, h) => t - h).ToArray();
        int[] grownFrames = Enumerable.Range(0, growth.Length).Where(i => growth[i] != 0).ToArray();
        output.WriteLine($"no light {noLight.Sum()} B, torch {torch.Sum()} B, +{torch.Sum() - noLight.Sum()} B over 200 snapshots");
        output.WriteLine($"frames that grew: {grownFrames.Length}: {string.Join(", ", grownFrames.Take(20).Select(i => $"#{i + 1} {growth[i]:+0;-0} B"))}");
        string first = torchJson[0];
        string firstNoLight = noLightJson[0];
        int at = 0;
        while (at < Math.Min(first.Length, firstNoLight.Length) && first[at] == firstNoLight[at])
        {
            at++;
        }

        output.WriteLine($"frame #1 differs at {at}: torch ...{first.Substring(Math.Max(0, at - 60), Math.Min(100, first.Length - Math.Max(0, at - 60)))}...");
        output.WriteLine($"frame #1 no light        ...{firstNoLight.Substring(Math.Max(0, at - 60), Math.Min(100, firstNoLight.Length - Math.Max(0, at - 60)))}...");

        Assert.True(torch.Sum() > noLight.Sum(), $"{torch.Sum()} vs {noLight.Sum()}");
        // Delta frames between keyframes carry nothing for an unchanged light: only the four keyframes grow.
        Assert.Equal(4, grownFrames.Length);
        Assert.All(grownFrames, i => Assert.Equal(0, i % 64));
        // Per frame the light costs at most one small field per record, never a whole actor.
        Assert.All(growth, delta => Assert.InRange(delta, -64, 64));
    }

    // Reads both sessions' inventories (which is what loads the kit and fills the equipped-summary cache
    // the snapshot's held light is read from) and asserts the shipped kit is where it should be.
    private static void LoadKits(WorldSessionRuntime runtime)
    {
        foreach (string session in Sessions)
        {
            Assert.Equal("tool.torch", OffHandKind(runtime, session));
        }
    }

    private static void UnequipTorches(WorldSessionRuntime runtime)
    {
        LoadKits(runtime);
        foreach (string session in Sessions)
        {
            AuthoritativeActionResult result = runtime.ProcessAuthoritativeAction(
                new PlayerActionMessage(new PlayerSessionId(session), 1, new InventoryUnequipIntent(OffHandSlot)));
            Assert.True(result.Accepted, result.Rejection?.Reason);
            Assert.Null(OffHandKind(runtime, session));
        }
    }

    private static string? OffHandKind(WorldSessionRuntime runtime, string session)
    {
        PlayerInventorySnapshot inventory = runtime.TryCaptureInventorySnapshot(new PlayerSessionId(session))!.Value;
        return (inventory.Equipment ?? []).SingleOrDefault(slot => slot.SlotIndex == OffHandSlot).Item?.ItemKind;
    }

    private static long[] Run(WorldSessionRuntime runtime, Kit kit) => Run(runtime, kit, out _);

    private static long[] Run(WorldSessionRuntime runtime, Kit kit, out List<string> json)
    {
        switch (kit)
        {
            case Kit.WornTorch:
                LoadKits(runtime);
                break;
            case Kit.NoTorch:
                UnequipTorches(runtime);
                break;
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

    private static readonly string[] Sessions = ["session-a", "session-b"];

    private static int OffHandSlot => EquipmentSlots.ToSlotIndex(EquipmentSlot.OffHand);

    private static WorldSessionRuntime CreateRuntime()
    {
        WorldSessionRuntime runtime = new(WorldDefaults.StartupFloorSize, new Vector3(5f, 1.5f, 5f), new SceneEventHub());
        runtime.AttachRemoteSession(new PlayerSessionId("session-a"), "session-a");
        runtime.AttachRemoteSession(new PlayerSessionId("session-b"), "session-b");
        return runtime;
    }
}
