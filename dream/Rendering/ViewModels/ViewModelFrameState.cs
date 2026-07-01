namespace Dream.Rendering.ViewModels;

public readonly record struct ViewModelFrameState(
    ViewModelSlotState Hands,
    ViewModelSlotState HeldItem);
