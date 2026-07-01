using Xunit;
using Dream.Multiplayer.Protocol;
using Dream.Rendering.Hud;
using Dream.Simulation.Runtime;

namespace Dream.Tests.Rendering.Hud;


public class AuthoritativeActionFeedbackSystemTests
{
    [Fact]
    public void Rejected_Action_Event_Captures_Latest_Message()
    {
        SceneEventHub hub = new();
        using AuthoritativeActionFeedbackSystem system = new(hub, displayDurationSeconds: 2f);

        hub.Publish(new AuthoritativeActionRejectedEvent(new AuthoritativeActionResult(
            "session-a",
            7,
            12,
            Accepted: false,
            Payload: null,
            Rejection: new RejectedUseDelta("Build blocked"))));

        Assert.Equal("Build blocked", system.CurrentRejectionMessage);
    }

    [Fact]
    public void Update_Clears_Message_After_Display_Window()
    {
        SceneEventHub hub = new();
        using AuthoritativeActionFeedbackSystem system = new(hub, displayDurationSeconds: 1f);
        hub.Publish(new AuthoritativeActionRejectedEvent(new AuthoritativeActionResult(
            "session-a",
            8,
            13,
            Accepted: false,
            Payload: null,
            Rejection: new RejectedUseDelta("Rejected"))));

        system.Update(0.5f);
        Assert.Equal("Rejected", system.CurrentRejectionMessage);

        system.Update(0.6f);
        Assert.Null(system.CurrentRejectionMessage);
    }
}
