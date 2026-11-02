namespace Dream.Rendering.Hud;

// What the quest tracker and the NPC dialogue window show (M6 workstream B). One instance per scene,
// created with a parameterless constructor (the headless play scene fills fields by it). Contract stub:
// workstream B fills the log and the dialogue; DialogueOpen is what ClassicHud reads to give the dialogue
// window the character window's place.
public sealed class QuestHudState
{
    public bool DialogueOpen { get; private set; }
}
