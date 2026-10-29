using Dream.Multiplayer.Protocol;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Simulation.Runtime;

// Persistence wave B2: the runtime builds its map from a WorldDescriptor, and the join-accepted
// message carries that descriptor.
public sealed class WorldDescriptorRuntimeTests
{
    private static readonly Vector3 PlayerStart = new(5f, 1.5f, 5f);

    [Fact]
    public void DefaultDescriptor_BuildsTheSameMapAsTheFloorSizeConstructor()
    {
        using WorldSessionRuntime fromDescriptor = new(WorldDescriptor.Default, PlayerStart, new SceneEventHub());
        using WorldSessionRuntime fromFloorSize = new(
            WorldDefaults.StartupFloorSize, PlayerStart, new SceneEventHub());

        Assert.Equal(WorldDescriptor.Default, fromDescriptor.World);
        Assert.Equal(WorldDescriptor.Default, fromFloorSize.World);
        AssertSameMap(fromFloorSize, fromDescriptor);
    }

    [Fact]
    public void DefaultDescriptor_MatchesAMapBuiltTheWayHostsBuiltItBeforeWorldDescriptors()
    {
        // Today's CreateMap(56): the startup terrain at the startup floor size, headless.
        Dream.Simulation.Physics.PhysicsIntegrator legacyPhysics = new();
        Map legacy = new(
            legacyPhysics,
            generateStartupTerrain: true,
            width: WorldDefaults.StartupFloorSize,
            depth: WorldDefaults.StartupFloorSize,
            headless: true);

        using WorldSessionRuntime runtime = new(WorldDescriptor.Default, PlayerStart, new SceneEventHub());

        Assert.Equal(legacy.Width, runtime.Map.Width);
        Assert.Equal(legacy.Height, runtime.Map.Height);
        Assert.Equal(legacy.Depth, runtime.Map.Depth);
        Assert.Equal(legacy.GeometryCells.Keys.ToArray(), runtime.Map.GeometryCells.Keys.ToArray());
        Assert.Equal(legacyPhysics.MapCubeBodies.Count, runtime.Physics.MapCubeBodies.Count);
        legacyPhysics.Dispose();
    }

    [Fact]
    public void SmallDescriptor_BuildsAMapOfThatSize()
    {
        WorldDescriptor small = WorldDescriptor.ForFloorSize(20, new WorldId(7), "small");

        using WorldSessionRuntime runtime = new(small, PlayerStart, new SceneEventHub());

        Assert.Same(small, runtime.World);
        Assert.Equal(20, runtime.Map.Width);
        Assert.Equal(8, runtime.Map.Height);
        Assert.Equal(20, runtime.Map.Depth);
    }

    [Fact]
    public void RebuildMapForFloorSize_UpdatesTheDescriptorKeepingIdAndName()
    {
        WorldDescriptor small = WorldDescriptor.ForFloorSize(20, new WorldId(7), "small");
        using WorldSessionRuntime runtime = new(small, PlayerStart, new SceneEventHub());

        runtime.RebuildMapForFloorSize(30);

        Assert.Equal(new WorldId(7), runtime.World.Id);
        Assert.Equal("small", runtime.World.Name);
        Assert.Equal(30, runtime.World.Width);
        Assert.Equal(30, runtime.World.Depth);
        Assert.Equal(30, runtime.Map.Width);
    }

    [Fact]
    public void NullDescriptor_IsRejected()
    {
        Assert.Throws<ArgumentNullException>(
            () => new WorldSessionRuntime((WorldDescriptor)null!, PlayerStart, new SceneEventHub()));
    }

    [Fact]
    public void DefaultHost_JoinAcceptedCarriesTheDefaultDescriptor()
    {
        using RuntimeAuthoritativeSessionHost host = RuntimeAuthoritativeSessionHost.CreateDefault();

        JoinAcceptedMessage accepted = host.JoinSession("session-a", "alice");

        Assert.NotNull(accepted.World);
        Assert.Equal(WorldDescriptorWire.From(WorldDescriptor.Default), accepted.World);
    }

    [Fact]
    public void HostFromPorts_JoinAcceptedCarriesThePortsDescriptor_CachedAcrossJoins()
    {
        WorldDescriptor small = WorldDescriptor.ForFloorSize(20, new WorldId(1), "small");
        RuntimePersistencePorts ports = new(null, null, null, null, null, null, null) { Descriptor = small };
        using RuntimeAuthoritativeSessionHost host = RuntimeAuthoritativeSessionHost.CreateDefault(ports);

        JoinAcceptedMessage first = host.JoinSession("session-a", "alice");
        JoinAcceptedMessage rejoin = host.JoinSession("session-a", "alice");
        JoinAcceptedMessage second = host.JoinSession("session-b", "bob");

        Assert.Equal(WorldDescriptorWire.From(small), first.World);
        Assert.Equal(20, first.World!.Width);
        Assert.Equal(20, host.Runtime.Map.Width);
        Assert.Same(first.World, rejoin.World);
        Assert.Same(first.World, second.World);
    }

    [Fact]
    public void JoinAccepted_AfterAMapRebuild_CarriesTheNewSize()
    {
        using RuntimeAuthoritativeSessionHost host = RuntimeAuthoritativeSessionHost.CreateDefault();
        JoinAcceptedMessage before = host.JoinSession("session-a", "alice");

        host.Runtime.RebuildMapForFloorSize(30);
        JoinAcceptedMessage after = host.JoinSession("session-a", "alice");

        Assert.Equal(WorldDefaults.StartupFloorSize, before.World!.Width);
        Assert.Equal(30, after.World!.Width);
        Assert.Equal(30, after.World.Depth);
    }

    [Fact]
    public void WorldId_NeverAppearsInTheWorldSnapshot()
    {
        using WorldSessionRuntime defaultWorld = new(
            WorldDescriptor.ForFloorSize(WorldDefaults.StartupFloorSize, new WorldId(1)), PlayerStart, new SceneEventHub());
        using WorldSessionRuntime otherId = new(
            WorldDescriptor.ForFloorSize(WorldDefaults.StartupFloorSize, new WorldId(9), "other"), PlayerStart, new SceneEventHub());

        System.Text.Json.JsonSerializerOptions options = new();
        options.Converters.Add(new WorldSnapshotJsonConverter());
        Assert.Equal(
            System.Text.Json.JsonSerializer.Serialize(defaultWorld.ExportWorldSnapshot(), options),
            System.Text.Json.JsonSerializer.Serialize(otherId.ExportWorldSnapshot(), options));
    }

    private static void AssertSameMap(WorldSessionRuntime expected, WorldSessionRuntime actual)
    {
        Assert.Equal(expected.Map.Width, actual.Map.Width);
        Assert.Equal(expected.Map.Height, actual.Map.Height);
        Assert.Equal(expected.Map.Depth, actual.Map.Depth);
        Assert.Equal(
            expected.Map.GeometryCells.Keys.ToArray(),
            actual.Map.GeometryCells.Keys.ToArray());
        Assert.Equal(expected.Physics.MapCubeBodies.Count, actual.Physics.MapCubeBodies.Count);
    }
}
