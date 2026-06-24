using OpenTK.Mathematics;

public static class ViewModelAssets
{
    public static ViewModelDefinition DebugHandsDefinition => new(
        "hands.debug",
        ViewModelSlot.Hands,
        AssetPaths.Animation("viewmodels/DebugHands.gltf"),
        "Idle",
        "Use",
        new Vector3(0f, -0.35f, -0.55f),
        new Vector3(8f, 180f, 0f),
        0.55f);

    public static ViewModelDefinition DebugHeldItemDefinition => new(
        "held.debug",
        ViewModelSlot.HeldItem,
        AssetPaths.Animation("viewmodels/DebugHeldItem.gltf"),
        "Idle",
        "Use",
        new Vector3(0.28f, -0.28f, -0.48f),
        new Vector3(0f, 20f, 0f),
        0.35f);
}
