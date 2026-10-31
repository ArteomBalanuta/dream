using Dream.Application;

namespace Dream.Rendering.ViewModels;

public class PlayerViewModelController
{
    private readonly ViewModelCatalog _catalog;
    private readonly GameSettings _settings;
    private ViewModelLoadout _loadout;
    // Null: nothing in the off hand. Held only here so ViewModelLoadout stays the two-slot default.
    private string? _offHandItemId;

    public PlayerViewModelController(ViewModelCatalog catalog, GameSettings settings)
    {
        _catalog = catalog;
        _settings = settings;
        _loadout = catalog.DefaultLoadout;
        CurrentFrameState = BuildState(ViewModelAnimationRequest.Idle, ViewModelAnimationRequest.Idle);
    }

    public ViewModelFrameState CurrentFrameState { get; private set; }

    public void TriggerPrimaryUse()
    {
        CurrentFrameState = BuildState(ViewModelAnimationRequest.Use, ViewModelAnimationRequest.Use);
    }

    public void SetHeldItem(string heldItemId)
    {
        if (string.Equals(_loadout.HeldItemId, heldItemId, StringComparison.Ordinal))
        {
            return;
        }

        _loadout = _loadout with { HeldItemId = heldItemId };
        CurrentFrameState = BuildState(CurrentFrameState.Hands.AnimationRequest, ViewModelAnimationRequest.Idle);
    }

    // The off-hand item is the same definition as a main-hand one, mirrored, and always Idle.
    public void SetOffHandItem(string? heldItemId)
    {
        if (string.Equals(_offHandItemId, heldItemId, StringComparison.Ordinal))
        {
            return;
        }

        _offHandItemId = heldItemId;
        CurrentFrameState = BuildState(
            CurrentFrameState.Hands.AnimationRequest,
            CurrentFrameState.HeldItem.AnimationRequest);
    }

    public void NotifyAnimationFinished(ViewModelSlot slot)
    {
        if (slot == ViewModelSlot.Hands)
        {
            CurrentFrameState = BuildState(
                ViewModelAnimationRequest.Idle,
                CurrentFrameState.HeldItem.AnimationRequest);
            return;
        }

        CurrentFrameState = BuildState(
            CurrentFrameState.Hands.AnimationRequest,
            ViewModelAnimationRequest.Idle);
    }

    public void RefreshPose()
    {
        CurrentFrameState = new ViewModelFrameState(
            RebuildSlotState(CurrentFrameState.Hands.Definition, CurrentFrameState.Hands.AnimationRequest),
            RebuildSlotState(CurrentFrameState.HeldItem.Definition, CurrentFrameState.HeldItem.AnimationRequest),
            CurrentFrameState.OffHandItem is { } offHand
                ? RebuildSlotState(offHand.Definition, ViewModelAnimationRequest.Idle)
                : null);
    }

    private ViewModelFrameState BuildState(
        ViewModelAnimationRequest handsRequest,
        ViewModelAnimationRequest heldItemRequest)
    {
        return new ViewModelFrameState(
            RebuildSlotState(_catalog.GetDefinition(_loadout.HandsId), handsRequest),
            RebuildSlotState(_catalog.Resolve(_loadout.HeldItemId), heldItemRequest),
            _offHandItemId is null
                ? null
                : RebuildSlotState(Mirror(_catalog.Resolve(_offHandItemId)), ViewModelAnimationRequest.Idle));
    }

    // X position and Y and Z rotation flip; the slot marks it as the off hand for the pose.
    private static ViewModelDefinition Mirror(ViewModelDefinition definition)
    {
        return definition with
        {
            Slot = ViewModelSlot.OffHand,
            PositionOffset = new OpenTK.Mathematics.Vector3(
                -definition.PositionOffset.X,
                definition.PositionOffset.Y,
                definition.PositionOffset.Z),
            RotationOffsetDegrees = new OpenTK.Mathematics.Vector3(
                definition.RotationOffsetDegrees.X,
                -definition.RotationOffsetDegrees.Y,
                -definition.RotationOffsetDegrees.Z)
        };
    }

    private ViewModelSlotState RebuildSlotState(
        ViewModelDefinition definition,
        ViewModelAnimationRequest animationRequest)
    {
        return new ViewModelSlotState(
            definition,
            animationRequest,
            ViewModelPoseResolver.Resolve(definition, _settings.ViewModelRig));
    }
}
