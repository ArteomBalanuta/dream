namespace Dream.Rendering.Hud;

// What the trade window shows (M5 workstream A): both offers, the locks and the confirms. One instance per
// scene, created with a parameterless constructor (the headless play scene fills fields by it). Contract
// stub: workstream A fills the offers; IsOpen is what ClassicHud reads to give the window the character
// window's place.
public sealed class TradeHudState
{
    public bool IsOpen { get; private set; }
}
