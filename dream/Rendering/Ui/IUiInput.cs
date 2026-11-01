using OpenTK.Mathematics;

namespace Dream.Rendering.Ui;

public readonly record struct UiPointer(Vector2 Position, bool LeftDown, bool RightDown, bool Interactive);

// Immediate-mode input. Panels call these while building; the router answers from the pointer and
// the hot id left by the previous frame.
public interface IUiInput
{
    // Click on release.
    bool Button(int id, UiRect r);

    bool Hovered(int id, UiRect r);

    bool RightClicked(int id, UiRect r);

    bool BeginDrag(int id, UiRect r, int payload);

    bool TryEndDrag(out int payload, out Vector2 dropPosition);

    // -1 when nothing is dragged.
    int ActiveDragPayload { get; }

    bool ModalActive { get; set; }

    // The pointer this frame. Position is NaN when the input cannot tell where it is.
    UiPointer Pointer { get; }
}
