namespace Dream.Rendering.Ui.Menu;

// What the loading screen shows this frame. Stage: the line above the bar. Progress 0..1 (the panel
// clamps it). Ready: the bar is full and the prompt replaces the stage. Tip: a flavour line under the
// panel, may be null. Seconds: drives the prompt's pulse.
public readonly record struct LoadingScreenState(string Stage, float Progress, bool Ready, string? Tip = null, float Seconds = 0f);
