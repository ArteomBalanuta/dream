namespace Dream.Configuration;

// How the game window occupies the screen (config key window_mode).
// Windowed: a normal bordered window. Borderless: an undecorated window covering the current monitor at its desktop
// resolution (no mode switch, instant alt-tab). Fullscreen: exclusive fullscreen on the current monitor at its current
// video mode.
public enum WindowMode : byte
{
    Windowed,
    Borderless,
    Fullscreen
}
