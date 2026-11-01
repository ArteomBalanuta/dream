using OpenTK.Mathematics;
using Dream.Simulation.Combat;

namespace Dream.Rendering.Hud;

// The first nine fields are what the ImGui path reads. The trailing ones are for the sprite (classic)
// path and default to values that mean nothing to ImGui:
//   Kind         - the hit kind (Regular, Critical or Incoming).
//   AgeSeconds   - age on the classic 1.4 s timeline (real age scaled by motion speed / 0.6).
//   SpawnOrder   - increases with every spawn, so the largest is the newest.
//   TargetId     - the actor the number belongs to, for the per-target pitch guard.
//   SizeFactor   - the size tuning relative to its default (SizeMultiplier / 2.35).
//   ScreenAnchor - pixel position of WorldAnchor (no offsets), filled by the renderer when it
//                  projects the list; the sprite panel adds the rise and the jitter.
public readonly record struct FloatingCombatTextRenderState(
    string Text,
    Vector3 WorldAnchor,
    float HorizontalOffset,
    float VerticalOffset,
    float DepthOffset,
    float Scale,
    float Alpha,
    FloatingCombatTextStyle Style,
    int LayerPriority,
    FloatingCombatTextKind Kind = FloatingCombatTextKind.Regular,
    float AgeSeconds = 0f,
    long SpawnOrder = 0,
    int TargetId = 0,
    float SizeFactor = 1f,
    Vector2 ScreenAnchor = default);
