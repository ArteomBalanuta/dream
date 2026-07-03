using OpenTK.Mathematics;
using Dream.Assets;

namespace Dream.Rendering.ViewModels;


public static class ViewModelAssets
{
    public static readonly ViewModelDefinition DebugHandsDefinition = new(
        "hands.debug",
        ViewModelSlot.Hands,
        AssetPaths.Animation("viewmodels/DebugHands.gltf"),
        "Idle",
        "Use",
        new Vector3(0f, -0.18f, -0.42f),
        new Vector3(12f, 180f, -4f),
        0.55f);

    public static readonly ViewModelDefinition DebugHeldItemDefinition = new(
        "held.debug",
        ViewModelSlot.HeldItem,
        AssetPaths.Animation("viewmodels/DebugHeldItem.gltf"),
        "Idle",
        "Use",
        new Vector3(0.16f, -0.12f, -0.36f),
        new Vector3(-2f, 16f, -6f),
        0.35f);

    public static readonly ViewModelDefinition BuilderToolDefinition = new(
        "held.tool.builder",
        ViewModelSlot.HeldItem,
        AssetPaths.Animation("viewmodels/DebugHeldItem.gltf"),
        "Idle",
        "Use",
        new Vector3(0.16f, -0.12f, -0.36f),
        new Vector3(-2f, 16f, -6f),
        0.35f);

    public static readonly ViewModelDefinition BladeToolDefinition = new(
        "held.tool.blade",
        ViewModelSlot.HeldItem,
        AssetPaths.Animation("viewmodels/DebugHeldItem.gltf"),
        "Idle",
        "Use",
        new Vector3(0.21f, -0.18f, -0.28f),
        new Vector3(-12f, 28f, -18f),
        0.28f);

    public static readonly ViewModelDefinition MaulToolDefinition = new(
        "held.tool.maul",
        ViewModelSlot.HeldItem,
        AssetPaths.Animation("viewmodels/DebugHeldItem.gltf"),
        "Idle",
        "Use",
        new Vector3(0.22f, -0.06f, -0.44f),
        new Vector3(10f, -4f, 8f),
        0.42f);

    public static readonly ViewModelDefinition LanternToolDefinition = new(
        "held.tool.lantern",
        ViewModelSlot.HeldItem,
        AssetPaths.Animation("viewmodels/DebugHeldItem.gltf"),
        "Idle",
        "Use",
        new Vector3(0.13f, -0.08f, -0.30f),
        new Vector3(4f, 36f, -8f),
        0.24f);

    public static readonly IReadOnlyList<ViewModelDefinition> BuiltInDefinitions =
        Array.AsReadOnly(new[]
        {
            DebugHandsDefinition,
            DebugHeldItemDefinition,
            BuilderToolDefinition,
            BladeToolDefinition,
            MaulToolDefinition,
            LanternToolDefinition
        });

    public static readonly ViewModelLoadout DefaultLoadout = new(
        DebugHandsDefinition.Id,
        BuilderToolDefinition.Id);
}
