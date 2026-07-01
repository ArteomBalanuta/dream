using OpenTK.Mathematics;

namespace Dream.Rendering.Actors;


public readonly record struct WorldActorLabelState(
    Vector3 WorldAnchor,
    string DisplayName,
    string HealthLabel);
