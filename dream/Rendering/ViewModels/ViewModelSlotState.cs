namespace Dream.Rendering.ViewModels;

public readonly record struct ViewModelSlotState(
    ViewModelDefinition Definition,
    ViewModelAnimationRequest AnimationRequest,
    ViewModelResolvedPose ResolvedPose);
