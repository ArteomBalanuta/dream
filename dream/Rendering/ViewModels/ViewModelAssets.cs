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

    // Wave T2: reuses the debug held item until a torch model exists.
    public static readonly ViewModelDefinition TorchToolDefinition = new(
        "held.tool.torch",
        ViewModelSlot.HeldItem,
        AssetPaths.Animation("viewmodels/DebugHeldItem.gltf"),
        "Idle",
        "Use",
        new Vector3(0.14f, -0.10f, -0.32f),
        new Vector3(-6f, 12f, -4f),
        0.26f);

    // The main hand is empty: nothing is drawn. The empty ModelPath is how the renderer knows;
    // it is kept out of BuiltInDefinitions because that list is preloaded as model paths.
    public const string EmptyHeldItemId = "held.none";

    public static readonly ViewModelDefinition EmptyHeldItemDefinition = new(
        EmptyHeldItemId,
        ViewModelSlot.HeldItem,
        string.Empty,
        null,
        null,
        Vector3.Zero,
        Vector3.Zero,
        1f);

    public static readonly IReadOnlyList<ViewModelDefinition> BuiltInDefinitions =
        Array.AsReadOnly(new[]
        {
            DebugHandsDefinition,
            DebugHeldItemDefinition,
            BuilderToolDefinition,
            BladeToolDefinition,
            MaulToolDefinition,
            LanternToolDefinition,
            TorchToolDefinition
        });

    // The MAIN-HAND view model shown before the first snapshot arrives, and nothing else: a loadout
    // has no off-hand field, and the one consumer (PlayScene.Replication) hard-codes
    // HeldLightKind.None for it, so a torch here would cast no light and would only make the
    // opening frames show a torch that pops to the hammer. The authoritative kit - hammer in main,
    // torch in off - is server-side, in PlayerInventoryService.EquipStarterKitWells.
    public static readonly ViewModelLoadout DefaultLoadout = new(
        DebugHandsDefinition.Id,
        BuilderToolDefinition.Id);
}
