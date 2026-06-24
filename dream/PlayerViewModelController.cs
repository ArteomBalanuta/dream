public class PlayerViewModelController
{
    private readonly ViewModelCatalog _catalog;
    private ViewModelLoadout _loadout;

    public PlayerViewModelController(ViewModelCatalog catalog)
    {
        _catalog = catalog;
        _loadout = catalog.DefaultLoadout;
        CurrentFrameState = BuildState(ViewModelAnimationRequest.Idle, ViewModelAnimationRequest.Idle);
    }

    public ViewModelFrameState CurrentFrameState { get; private set; }

    public void TriggerPrimaryUse()
    {
        CurrentFrameState = BuildState(ViewModelAnimationRequest.Use, ViewModelAnimationRequest.Use);
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

    private ViewModelFrameState BuildState(
        ViewModelAnimationRequest handsRequest,
        ViewModelAnimationRequest heldItemRequest)
    {
        return new ViewModelFrameState(
            new ViewModelSlotState(_catalog.GetDefinition(_loadout.HandsId), handsRequest),
            new ViewModelSlotState(_catalog.GetDefinition(_loadout.HeldItemId), heldItemRequest));
    }
}
