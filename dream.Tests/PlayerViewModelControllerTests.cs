using Xunit;

public class PlayerViewModelControllerTests
{
    [Fact]
    public void CreateDefaultFrameState_UsesCatalogLoadout()
    {
        ViewModelCatalog catalog = ViewModelCatalog.CreateDefault();
        PlayerViewModelController controller = new(catalog);

        ViewModelFrameState state = controller.CurrentFrameState;

        Assert.Equal("hands.debug", state.Hands.Definition.Id);
        Assert.Equal("held.debug", state.HeldItem.Definition.Id);
    }

    [Fact]
    public void TriggerPrimaryUse_StartsUseAnimationOnHands()
    {
        ViewModelCatalog catalog = ViewModelCatalog.CreateDefault();
        PlayerViewModelController controller = new(catalog);

        controller.TriggerPrimaryUse();

        Assert.Equal(ViewModelAnimationRequest.Use, controller.CurrentFrameState.Hands.AnimationRequest);
    }

    [Fact]
    public void FinishOneShot_ReturnsHandsToIdle()
    {
        ViewModelCatalog catalog = ViewModelCatalog.CreateDefault();
        PlayerViewModelController controller = new(catalog);

        controller.TriggerPrimaryUse();
        controller.NotifyAnimationFinished(ViewModelSlot.Hands);

        Assert.Equal(ViewModelAnimationRequest.Idle, controller.CurrentFrameState.Hands.AnimationRequest);
    }
}
