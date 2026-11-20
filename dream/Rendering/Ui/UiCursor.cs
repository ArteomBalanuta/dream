using OpenTK.Mathematics;

namespace Dream.Rendering.Ui;

public enum UiCursorKind : byte { Pointer, Pressed, Grab, Attack }

// The sprite cursor shared by the classic HUD and the front-end menus.
public static class UiCursor
{
    // Atlas pixels per UI unit of the cursors page; a test pins it to cursors.json "scale".
    public const float ArtPixelsPerUnit = 4f;

    // Grab while dragging, else Attack over an enemy, else Pressed while the left button is down, else Pointer.
    public static UiCursorKind Select(bool dragging, bool leftDown, bool overEnemy) => throw new NotImplementedException();

    public static string SpriteName(UiCursorKind kind) => throw new NotImplementedException();

    // The screen rect that puts the sprite's pivot on position (origin rounded), at the layout scale.
    public static UiRect Rect(in UiSprite sprite, Vector2 position, float layoutScale) => throw new NotImplementedException();

    // Draws kind at the pointer when it is interactive, finite and the sprite exists; true when drawn.
    public static bool Draw(IUiCanvas canvas, UiSpriteTable sprites, in UiPointer pointer, float layoutScale, UiCursorKind kind) => throw new NotImplementedException();
}
