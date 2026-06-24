using OpenTK.Mathematics;

public static class ViewModelAssets
{
    public static readonly ViewModelDefinition DebugHandsDefinition = new(
        "hands.debug",
        ViewModelSlot.Hands,
        AssetPaths.Animation("viewmodels/DebugHands.gltf"),
        "Idle",
        "Use",
        new Vector3(0f, -0.35f, -0.55f),
        new Vector3(8f, 180f, 0f),
        0.55f);

    public static readonly ViewModelDefinition DebugHeldItemDefinition = new(
        "held.debug",
        ViewModelSlot.HeldItem,
        AssetPaths.Animation("viewmodels/DebugHeldItem.gltf"),
        "Idle",
        "Use",
        new Vector3(0.28f, -0.28f, -0.48f),
        new Vector3(0f, 20f, 0f),
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
