using Dream.Application;

namespace Dream.Rendering.ViewModels;

public class PlayerViewModelController
{
    private readonly ViewModelCatalog _catalog;
    private readonly GameSettings _settings;
    private ViewModelLoadout _loadout;

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
            RebuildSlotState(CurrentFrameState.HeldItem.Definition, CurrentFrameState.HeldItem.AnimationRequest));
    }

    private ViewModelFrameState BuildState(
        ViewModelAnimationRequest handsRequest,
        ViewModelAnimationRequest heldItemRequest)
    {
        return new ViewModelFrameState(
            RebuildSlotState(_catalog.GetDefinition(_loadout.HandsId), handsRequest),
            RebuildSlotState(_catalog.GetDefinition(_loadout.HeldItemId), heldItemRequest));
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
