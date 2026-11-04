using OpenTK.Mathematics;
using Xunit;
using Dream.Application;
using Dream.Rendering.ViewModels;

namespace Dream.Tests.Rendering.ViewModels;

// Wave H3 (H-D15): the off-hand viewmodel is the main-hand definition mirrored. Whether it looks
// right on screen needs a person; these pin the numbers.
public class ViewModelOffHandPoseTests
{
    [Fact]
    public void OffHand_MirrorsTheMainHandPose_WithDefaultTuning()
    {
        ViewModelRigSettings rig = ViewModelRigSettings.CreateDefault();
        rig.PositionOffset = Vector3.Zero;
        rig.RotationOffsetDegrees = Vector3.Zero;
        ViewModelDefinition main = ViewModelAssets.BladeToolDefinition;
        ViewModelDefinition off = main with
        {
            Slot = ViewModelSlot.OffHand,
            PositionOffset = new Vector3(-main.PositionOffset.X, main.PositionOffset.Y, main.PositionOffset.Z),
            RotationOffsetDegrees = new Vector3(main.RotationOffsetDegrees.X, -main.RotationOffsetDegrees.Y, -main.RotationOffsetDegrees.Z)
        };

        ViewModelResolvedPose mainPose = ViewModelPoseResolver.Resolve(main, rig);
        ViewModelResolvedPose offPose = ViewModelPoseResolver.Resolve(off, rig);

        // The anchor term (spread, drop, pull) is not mirrored, so compare the definition's part.
        float spread = rig.BodyAnchor.HorizontalSpread;
        Assert.Equal(-(mainPose.PositionOffset.X - spread), offPose.PositionOffset.X + spread, 4);
        Assert.Equal(mainPose.PositionOffset.Y, offPose.PositionOffset.Y, 5);
        Assert.Equal(mainPose.PositionOffset.Z, offPose.PositionOffset.Z, 5);
        Assert.Equal(mainPose.RotationOffsetDegrees.X, offPose.RotationOffsetDegrees.X, 5);
        Assert.Equal(-mainPose.RotationOffsetDegrees.Y, offPose.RotationOffsetDegrees.Y, 5);
        Assert.Equal(-mainPose.RotationOffsetDegrees.Z, offPose.RotationOffsetDegrees.Z, 5);
        Assert.Equal(mainPose.Scale, offPose.Scale);
    }

    [Fact]
    public void OffHand_MirrorsTheHeldItemTuning()
    {
        ViewModelRigSettings rig = ViewModelRigSettings.CreateDefault();
        rig.PositionOffset = Vector3.Zero;
        rig.RotationOffsetDegrees = Vector3.Zero;
        rig.BodyAnchor.HorizontalSpread = 0f;
        rig.BodyAnchor.VerticalDrop = 0f;
        rig.BodyAnchor.ForwardPull = 0f;
        rig.HeldItem.PositionOffset = new Vector3(0.10f, 0.02f, 0.03f);
        rig.HeldItem.RotationOffsetDegrees = new Vector3(1f, 2f, 3f);
        ViewModelDefinition off = ViewModelAssets.DebugHeldItemDefinition with
        {
            Slot = ViewModelSlot.OffHand,
            PositionOffset = Vector3.Zero,
            RotationOffsetDegrees = Vector3.Zero
        };

        ViewModelResolvedPose pose = ViewModelPoseResolver.Resolve(off, rig);

        Assert.Equal(-0.10f, pose.PositionOffset.X, 5);
        Assert.Equal(0.02f, pose.PositionOffset.Y, 5);
        Assert.Equal(1f, pose.RotationOffsetDegrees.X, 5);
        Assert.Equal(-2f, pose.RotationOffsetDegrees.Y, 5);
        Assert.Equal(-3f, pose.RotationOffsetDegrees.Z, 5);
    }

    [Fact]
    public void OffHand_AnchorUsesTheHandsSide()
    {
        ViewModelRigSettings rig = ViewModelRigSettings.CreateDefault();
        rig.BodyAnchor.HorizontalSpread = 0.5f;
        ViewModelDefinition main = ViewModelAssets.DebugHeldItemDefinition;
        ViewModelDefinition off = main with { Slot = ViewModelSlot.OffHand };

        float offX = ViewModelPoseResolver.Resolve(off, rig).PositionOffset.X;
        float mainX = ViewModelPoseResolver.Resolve(main, rig).PositionOffset.X;

        Assert.True(offX < mainX);
    }
}

public class PlayerViewModelControllerEquipmentTests
{
    private static PlayerViewModelController Create() =>
        new(ViewModelCatalog.CreateDefault(), new GameSettings(new Vector2i(1440, 1024)));

    private static ViewModelSlotState OffHandOf(PlayerViewModelController controller) =>
        controller.CurrentFrameState.OffHandItem ?? throw new InvalidOperationException("No off-hand item.");

    [Fact]
    public void DefaultFrameState_HasNoOffHand()
    {
        PlayerViewModelController controller = Create();

        Assert.Null(controller.CurrentFrameState.OffHandItem);
        Assert.Equal("held.tool.torch", controller.CurrentFrameState.HeldItem.Definition.Id);
    }

    [Fact]
    public void SetHeldItem_UnregisteredVisual_ShowsTheDebugItem_InsteadOfThrowing()
    {
        PlayerViewModelController controller = Create();

        controller.SetHeldItem("visual.nobody-registered");

        Assert.Equal("held.debug", controller.CurrentFrameState.HeldItem.Definition.Id);
    }

    [Fact]
    public void SetOffHandItem_AddsAMirroredIdleItem_AndNullRemovesIt()
    {
        PlayerViewModelController controller = Create();
        controller.SetHeldItem("held.tool.blade");

        controller.SetOffHandItem("held.tool.torch");

        ViewModelSlotState off = OffHandOf(controller);
        ViewModelSlotState main = controller.CurrentFrameState.HeldItem;
        Assert.Equal("held.tool.torch", off.Definition.Id);
        Assert.Equal(ViewModelSlot.OffHand, off.Definition.Slot);
        Assert.Equal(ViewModelAnimationRequest.Idle, off.AnimationRequest);
        Assert.True(off.ResolvedPose.PositionOffset.X < 0f);
        Assert.True(main.ResolvedPose.PositionOffset.X > 0f);
        Assert.Equal(ViewModelAssets.TorchToolDefinition.RotationOffsetDegrees.Y, -off.Definition.RotationOffsetDegrees.Y);

        controller.SetOffHandItem(null);

        Assert.Null(controller.CurrentFrameState.OffHandItem);
    }

    [Fact]
    public void BladePlusTorch_OnlyTheMainHandPlaysUse()
    {
        PlayerViewModelController controller = Create();
        controller.SetHeldItem("held.tool.blade");
        controller.SetOffHandItem("held.tool.torch");

        controller.TriggerPrimaryUse();

        Assert.Equal(ViewModelAnimationRequest.Use, controller.CurrentFrameState.HeldItem.AnimationRequest);
        Assert.Equal(ViewModelAnimationRequest.Idle, OffHandOf(controller).AnimationRequest);

        controller.NotifyAnimationFinished(ViewModelSlot.HeldItem);

        Assert.Equal(ViewModelAnimationRequest.Idle, controller.CurrentFrameState.HeldItem.AnimationRequest);
        Assert.NotNull(controller.CurrentFrameState.OffHandItem);
    }

    [Fact]
    public void SwappingTheMainHandKeepsTheOffHand_AndRefreshPoseKeepsItMirrored()
    {
        PlayerViewModelController controller = Create();
        controller.SetOffHandItem("held.tool.lantern");
        controller.SetHeldItem("held.tool.maul");
        float before = OffHandOf(controller).ResolvedPose.PositionOffset.X;

        controller.RefreshPose();

        Assert.Equal("held.tool.lantern", OffHandOf(controller).Definition.Id);
        Assert.Equal(before, OffHandOf(controller).ResolvedPose.PositionOffset.X);
    }

    [Fact]
    public void UnequipMain_ShowsNoItem_AndAUseRequestCannotStick()
    {
        PlayerViewModelController controller = Create();
        controller.SetHeldItem("held.tool.blade");

        controller.SetHeldItem(ViewModelAssets.EmptyHeldItemId);
        controller.TriggerPrimaryUse();

        ViewModelSlotState held = controller.CurrentFrameState.HeldItem;
        Assert.Equal("held.none", held.Definition.Id);
        // The renderer reports an empty slot finished, and the controller returns it to Idle.
        Assert.True(ViewModelRenderer.IsEmptySlot(held));
        controller.NotifyAnimationFinished(ViewModelSlot.HeldItem);
        Assert.Equal(ViewModelAnimationRequest.Idle, controller.CurrentFrameState.HeldItem.AnimationRequest);
    }

    [Fact]
    public void DefaultFrameState_IsUnchangedBySettingTheSameDefaults()
    {
        PlayerViewModelController controller = Create();
        ViewModelFrameState before = controller.CurrentFrameState;

        controller.SetHeldItem(ViewModelAssets.DefaultLoadout.HeldItemId);
        controller.SetOffHandItem(null);

        Assert.Equal(before, controller.CurrentFrameState);
    }
}
