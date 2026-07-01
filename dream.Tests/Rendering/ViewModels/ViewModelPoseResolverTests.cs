using OpenTK.Mathematics;
using Xunit;
using Dream.Rendering.ViewModels;

namespace Dream.Tests.Rendering.ViewModels;


public class ViewModelPoseResolverTests
{
    [Fact]
    public void ResolveHands_Composes_Shared_BodyAnchor_And_Local_Offsets()
    {
        ViewModelDefinition definition = new(
            "hands.debug",
            ViewModelSlot.Hands,
            "animations/viewmodels/debug-hands.glb",
            "Idle",
            "Use",
            new Vector3(0f, -0.35f, -0.55f),
            new Vector3(8f, 180f, 0f),
            0.55f);

        ViewModelRigSettings rig = new()
        {
            PositionOffset = new Vector3(0.10f, 0.05f, 0f),
            RotationOffsetDegrees = new Vector3(1f, 2f, 3f),
            Hands = new ViewModelSlotTuning
            {
                PositionOffset = new Vector3(0.02f, -0.04f, 0.01f),
                RotationOffsetDegrees = new Vector3(3f, 4f, 5f)
            },
            BodyAnchor = new ViewModelBodyAnchorSettings
            {
                HorizontalSpread = 0.45f,
                VerticalDrop = 0.28f,
                ForwardPull = 0.18f
            }
        };

        ViewModelResolvedPose resolved = ViewModelPoseResolver.Resolve(definition, rig);

        AssertVectorEqual(new Vector3(-0.33f, -0.62f, -0.72f), resolved.PositionOffset);
        AssertVectorEqual(new Vector3(12f, 186f, 8f), resolved.RotationOffsetDegrees);
        Assert.Equal(60f, resolved.FieldOfViewDegrees);
    }

    [Fact]
    public void ResolveHeldItem_Uses_Default_Local_And_BodyAnchor_Adjustments()
    {
        ViewModelDefinition definition = new(
            "held.debug",
            ViewModelSlot.HeldItem,
            "animations/viewmodels/debug-held.glb",
            "Idle",
            "Use",
            new Vector3(0.28f, -0.28f, -0.48f),
            new Vector3(0f, 20f, 0f),
            0.35f);

        ViewModelResolvedPose resolved = ViewModelPoseResolver.Resolve(definition, ViewModelRigSettings.CreateDefault());

        AssertVectorEqual(new Vector3(0.234f, -0.382f, -0.816f), resolved.PositionOffset);
    }

    [Fact]
    public void DefaultRig_Lowers_Hands_Without_Changing_Horizontal_Center()
    {
        ViewModelResolvedPose resolved = ViewModelPoseResolver.Resolve(
            ViewModelAssets.DebugHandsDefinition,
            ViewModelRigSettings.CreateDefault());

        Assert.Equal(ViewModelAssets.DebugHandsDefinition.PositionOffset.X, resolved.PositionOffset.X);
        Assert.True(resolved.PositionOffset.Y < ViewModelAssets.DebugHandsDefinition.PositionOffset.Y);
        Assert.True(resolved.PositionOffset.Z < ViewModelAssets.DebugHandsDefinition.PositionOffset.Z);
        Assert.Equal(60f, resolved.FieldOfViewDegrees);
    }

    private static void AssertVectorEqual(Vector3 expected, Vector3 actual, float tolerance = 0.001f)
    {
        Assert.InRange(actual.X, expected.X - tolerance, expected.X + tolerance);
        Assert.InRange(actual.Y, expected.Y - tolerance, expected.Y + tolerance);
        Assert.InRange(actual.Z, expected.Z - tolerance, expected.Z + tolerance);
    }
}
