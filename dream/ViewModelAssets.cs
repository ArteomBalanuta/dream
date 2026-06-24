using OpenTK.Mathematics;

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

    public static readonly IReadOnlyList<ViewModelDefinition> BuiltInDefinitions =
        Array.AsReadOnly(new[]
        {
            DebugHandsDefinition,
            DebugHeldItemDefinition
        });

    public static readonly ViewModelLoadout DefaultLoadout = new(
        DebugHandsDefinition.Id,
        DebugHeldItemDefinition.Id);
}
