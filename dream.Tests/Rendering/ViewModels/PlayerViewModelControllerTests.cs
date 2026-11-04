using Xunit;
using OpenTK.Mathematics;
using Dream.Application;
using Dream.Rendering.ViewModels;
using Dream.Simulation.Players;

namespace Dream.Tests.Rendering.ViewModels;


public class PlayerViewModelControllerTests
{
    [Fact]
    public void CreateDefaultFrameState_UsesCatalogLoadout()
    {
        ViewModelCatalog catalog = ViewModelCatalog.CreateDefault();
        GameSettings settings = new(new Vector2i(1440, 1024));
        PlayerViewModelController controller = new(catalog, settings);

        ViewModelFrameState state = controller.CurrentFrameState;

        Assert.Equal("hands.debug", state.Hands.Definition.Id);
        Assert.Equal("held.tool.builder", state.HeldItem.Definition.Id);
    }

    [Fact]
    public void SetHeldItem_Swaps_Held_Viewmodel_Definition()
    {
        ViewModelCatalog catalog = ViewModelCatalog.CreateDefault();
        GameSettings settings = new(new Vector2i(1440, 1024));
        PlayerViewModelController controller = new(catalog, settings);

        controller.SetHeldItem("held.tool.blade");

        Assert.Equal("held.tool.blade", controller.CurrentFrameState.HeldItem.Definition.Id);
    }

    [Fact]
    public void TriggerPrimaryUse_StartsUseAnimationOnHands()
    {
        ViewModelCatalog catalog = ViewModelCatalog.CreateDefault();
        GameSettings settings = new(new Vector2i(1440, 1024));
        PlayerViewModelController controller = new(catalog, settings);

        controller.TriggerPrimaryUse();

        Assert.Equal(ViewModelAnimationRequest.Use, controller.CurrentFrameState.Hands.AnimationRequest);
    }

    [Fact]
    public void FinishOneShot_ReturnsHandsToIdle()
    {
        ViewModelCatalog catalog = ViewModelCatalog.CreateDefault();
        GameSettings settings = new(new Vector2i(1440, 1024));
        PlayerViewModelController controller = new(catalog, settings);

        controller.TriggerPrimaryUse();
        controller.NotifyAnimationFinished(ViewModelSlot.Hands);

        Assert.Equal(ViewModelAnimationRequest.Idle, controller.CurrentFrameState.Hands.AnimationRequest);
    }

    [Fact]
    public void FinishOneShot_ReturnsHeldItemToIdle()
    {
        ViewModelCatalog catalog = ViewModelCatalog.CreateDefault();
        GameSettings settings = new(new Vector2i(1440, 1024));
        PlayerViewModelController controller = new(catalog, settings);

        controller.TriggerPrimaryUse();
        controller.NotifyAnimationFinished(ViewModelSlot.HeldItem);

        Assert.Equal(ViewModelAnimationRequest.Use, controller.CurrentFrameState.Hands.AnimationRequest);
        Assert.Equal(ViewModelAnimationRequest.Idle, controller.CurrentFrameState.HeldItem.AnimationRequest);
    }

    [Fact]
    public void CurrentFrameState_Contains_Resolved_Hands_Pose()
    {
        ViewModelCatalog catalog = ViewModelCatalog.CreateDefault();
        GameSettings settings = new(new Vector2i(1440, 1024));
        settings.ViewModelRig.BodyAnchor.HorizontalSpread = 0.60f;

        PlayerViewModelController controller = new(catalog, settings);

        Assert.True(controller.CurrentFrameState.Hands.ResolvedPose.PositionOffset.X < -0.35f);
        Assert.Equal(settings.ViewModelRig.FieldOfViewDegrees, controller.CurrentFrameState.Hands.ResolvedPose.FieldOfViewDegrees);
    }

    [Fact]
    public void Resetting_Settings_Changes_Resolved_Pose_On_Rebuild()
    {
        ViewModelCatalog catalog = ViewModelCatalog.CreateDefault();
        GameSettings settings = new(new Vector2i(1440, 1024));
        settings.ViewModelRig.Hands.PositionOffset = new Vector3(-0.25f, 0f, 0f);

        PlayerViewModelController controller = new(catalog, settings);
        float tunedX = controller.CurrentFrameState.Hands.ResolvedPose.PositionOffset.X;

        settings.ResetViewModelRig();
        controller.RefreshPose();

        Assert.NotEqual(tunedX, controller.CurrentFrameState.Hands.ResolvedPose.PositionOffset.X);
    }
}

public class PlayerInteractionTests
{
    [Fact]
    public void ShouldTriggerPrimaryUse_Fires_Immediately_Then_Repeats_On_Interval()
    {
        PlayerInteraction interaction = new();

        Assert.True(interaction.ShouldTriggerPrimaryUse(new PlayerInteractionCommand(true, false, false), 0.5f, 0f));
        Assert.False(interaction.ShouldTriggerPrimaryUse(new PlayerInteractionCommand(true, false, false), 0.5f, 0.25f));
        Assert.True(interaction.ShouldTriggerPrimaryUse(new PlayerInteractionCommand(true, false, false), 0.5f, 0.25f));
    }

    [Fact]
    public void ShouldTriggerPrimaryUse_Resets_When_Button_Is_Released()
    {
        PlayerInteraction interaction = new();

        Assert.True(interaction.ShouldTriggerPrimaryUse(new PlayerInteractionCommand(true, false, false), 0.5f, 0f));
        Assert.False(interaction.ShouldTriggerPrimaryUse(new PlayerInteractionCommand(false, false, false), 0.5f, 0.1f));
        Assert.True(interaction.ShouldTriggerPrimaryUse(new PlayerInteractionCommand(true, false, false), 0.5f, 0f));
    }
}
