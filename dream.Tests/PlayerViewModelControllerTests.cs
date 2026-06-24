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

    [Fact]
    public void FinishOneShot_ReturnsHeldItemToIdle()
    {
        ViewModelCatalog catalog = ViewModelCatalog.CreateDefault();
        PlayerViewModelController controller = new(catalog);

        controller.TriggerPrimaryUse();
        controller.NotifyAnimationFinished(ViewModelSlot.HeldItem);

        Assert.Equal(ViewModelAnimationRequest.Use, controller.CurrentFrameState.Hands.AnimationRequest);
        Assert.Equal(ViewModelAnimationRequest.Idle, controller.CurrentFrameState.HeldItem.AnimationRequest);
    }
}

public class PlayerInteractionTests
{
    [Fact]
    public void CapturePrimaryUse_ReturnsRisingEdgeOnly()
    {
        PlayerInteraction interaction = new();

        Assert.True(interaction.CapturePrimaryUse(new PlayerInteractionCommand(true)));
        Assert.False(interaction.CapturePrimaryUse(new PlayerInteractionCommand(true)));
        Assert.False(interaction.CapturePrimaryUse(new PlayerInteractionCommand(false)));
        Assert.True(interaction.CapturePrimaryUse(new PlayerInteractionCommand(true)));
    }
}
