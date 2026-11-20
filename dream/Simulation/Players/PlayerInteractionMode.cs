namespace Dream.Simulation.Players;

public enum PlayerInteractionMode
{
    Building,
    Combat
}

public static class PlayerInteractionModeExtensions
{
    // The mode a session starts in: what a new profile is created with, what the server gives a fresh
    // remote session, and what the client assumes until the first snapshot corrects it. All three used
    // to say Building independently, which is why changing one of them would not have changed anything.
    //
    // It is Combat because the left button only attacks in Combat (CursorAimController gates on it), so
    // a player who has not found the mode keys clicks an enemy and gets NOTHING - no swing, no miss, no
    // message - while their skills keep working, which reads as a broken enemy rather than a wrong mode.
    //
    // This is deliberately NOT the enum's own default. Building is the zero value, and the mode travels
    // the wire as an int (ActorFrameJson), so the declaration order is protocol: reordering the enum to
    // move the default would silently repoint every mode already stored or in flight.
    public const PlayerInteractionMode Default = PlayerInteractionMode.Combat;

    public static string ToHudLabel(this PlayerInteractionMode mode)
    {
        return mode switch
        {
            PlayerInteractionMode.Building => "Building",
            PlayerInteractionMode.Combat => "Combat",
            _ => mode.ToString()
        };
    }
}
