using Dream.Multiplayer.Protocol;

namespace Dream.Simulation.Runtime;

// Skills (M4 workstream B). Contract-commit stub: the action rejects until workstream B replaces this
// file.
public sealed partial class WorldSessionRuntime
{
    private AuthoritativeActionResult ProcessSkillUseAction(
        PlayerActionMessage message,
        AuthoritativeSessionActionContext context,
        SkillUseIntent intent)
    {
        return CreateRejectedActionResult(message, "Skills are not available yet.");
    }
}
