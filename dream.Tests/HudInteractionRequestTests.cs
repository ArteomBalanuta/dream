using OpenTK.Mathematics;
using Xunit;

public class HudInteractionRequestTests
{
    [Fact]
    public void Empty_Request_Has_No_Runtime_Changes()
    {
        HudInteractionRequest request = HudInteractionRequest.None;

        Assert.Null(request.FieldOfViewDegrees);
        Assert.Null(request.MouseSensitivity);
        Assert.Null(request.RequestedResolution);
        Assert.Null(request.StagedFloorSize);
        Assert.Null(request.RequestedFloorSize);
        Assert.Null(request.ShowCollisionGeometry);
    }

    [Fact]
    public void Request_Can_Carry_Multiple_Changes_Without_Applying_Them()
    {
        HudInteractionRequest request = new(
            FieldOfViewDegrees: 80f,
            MouseSensitivity: 4.5f,
            RequestedResolution: new Vector2i(1600, 900),
            StagedFloorSize: 20,
            RequestedFloorSize: 20,
            ShowCollisionGeometry: true);

        Assert.Equal(80f, request.FieldOfViewDegrees);
        Assert.Equal(4.5f, request.MouseSensitivity);
        Assert.Equal(new Vector2i(1600, 900), request.RequestedResolution);
        Assert.Equal(20, request.StagedFloorSize);
        Assert.Equal(20, request.RequestedFloorSize);
        Assert.True(request.ShowCollisionGeometry);
    }

    [Fact]
    public void Hud_Frame_Result_Preserves_The_Full_Request()
    {
        HudInteractionRequest request = new(
            FieldOfViewDegrees: 80f,
            MouseSensitivity: 4.5f,
            RequestedResolution: new Vector2i(1600, 900),
            StagedFloorSize: 20,
            RequestedFloorSize: 20,
            ShowCollisionGeometry: true);

        HudFrameResult result = new(request);

        Assert.Equal(request, result.Request);
    }
}
