using Dream.Client.Scenes;
using Xunit;

namespace Dream.Tests.Client.Scenes;

// The cam: capture steps: parsed into specs the scene executes, malformed ones dropped like any other bad step.
public class HudCaptureCameraStepTests
{
    [Fact]
    public void Yaw_Zoom_Pan_Parse_WithTheirValues()
    {
        Assert.True(HudCaptureStepSpec.TryParse("cam:yaw:135", out HudCaptureStepSpec yaw));
        Assert.Equal(HudCaptureVerb.CamYaw, yaw.Verb);
        Assert.Equal(135f, yaw.CamValueA, 3);

        Assert.True(HudCaptureStepSpec.TryParse("cam:yaw:-90.5", out HudCaptureStepSpec negative));
        Assert.Equal(-90.5f, negative.CamValueA, 3);

        Assert.True(HudCaptureStepSpec.TryParse("cam:zoom:24", out HudCaptureStepSpec zoom));
        Assert.Equal(HudCaptureVerb.CamZoom, zoom.Verb);
        Assert.Equal(24f, zoom.CamValueA, 3);

        Assert.True(HudCaptureStepSpec.TryParse("cam:pan:6:-4.25", out HudCaptureStepSpec pan));
        Assert.Equal(HudCaptureVerb.CamPan, pan.Verb);
        Assert.Equal(6f, pan.CamValueA, 3);
        Assert.Equal(-4.25f, pan.CamValueB, 3);
    }

    [Theory]
    [InlineData("cam:free", "CamFree")]
    [InlineData("cam:follow", "CamFollow")]
    [InlineData("cam:reset", "CamReset")]
    public void ModeSteps_Parse(string token, string verb)
    {
        Assert.True(HudCaptureStepSpec.TryParse(token, out HudCaptureStepSpec spec));
        Assert.Equal(verb, spec.Verb.ToString());
        Assert.True(spec.IsCameraStep);
    }

    [Theory]
    [InlineData("cam")]
    [InlineData("cam:")]
    [InlineData("cam:spin:10")]
    [InlineData("cam:yaw")]
    [InlineData("cam:yaw:abc")]
    [InlineData("cam:yaw:NaN")]
    [InlineData("cam:yaw:Infinity")]
    [InlineData("cam:yaw:10:20")]
    [InlineData("cam:zoom:0")]
    [InlineData("cam:zoom:-3")]
    [InlineData("cam:zoom")]
    [InlineData("cam:pan:5")]
    [InlineData("cam:pan:5:x")]
    [InlineData("cam:pan:1:2:3")]
    [InlineData("cam:free:1")]
    [InlineData("cam:follow:now")]
    [InlineData("cam:reset:1")]
    [InlineData("cam:yaw:1e30")]
    public void Malformed_AreRejected(string token)
    {
        Assert.False(HudCaptureStepSpec.TryParse(token, out HudCaptureStepSpec spec));
        Assert.Equal(default, spec);
    }

    [Fact]
    public void OtherSteps_AreNotCameraSteps()
    {
        Assert.True(HudCaptureStepSpec.TryParse("wait:5", out HudCaptureStepSpec wait));
        Assert.False(wait.IsCameraStep);
    }
}
