using OpenTK.Mathematics;

namespace Dream.Rendering.Lighting;

// One world-space point light. Color is linear RGB already multiplied by intensity. Radius is the
// distance where the light reaches 0 (windowed falloff in pointLights.glsl).
//
// FlickerSeed is the fire's stable flicker seed (FireFlicker.SeedFor for a placed fire, the actor id for
// a held one), or 0 for a light that is not a fire or whose producer does not know one. It is CPU-only:
// LightSelection fills the std140 LightList from Position, Radius and Color field by field, so the seed
// never reaches the GPU. It exists so a consumer that only has the light - the halo path through
// FireFlicker.IntensityOf - reads the SAME phase the producer sampled with, instead of re-deriving it
// from a position the flame's own wander has already moved.
//
// CarrierActorId is the actor holding a held light (the key the actor shadow casters are owned by: the server actor id),
// and 0 for every other light, a placed fire's FlickerSeed included. The fire shadow pass keeps that actor out of this
// light's own tiles; it is independent of FlickerSeed, which only picks the flicker phase.
//
// Flicker is the intensity multiplier the producer already applied to Color (1 = steady, the default). The fire shadow
// pass divides it back out to rank lights by their steady brightness, so two fires do not swap shadow slots as they flicker.
// Wander is the offset the producer added to the fire's resting position (Position - Wander is where the flame rests):
// the fire shadow pass ranks by the resting position, so a wandering flame does not swap shadow slots. Zero for a light that does not wander.
public readonly record struct PointLight(Vector3 Position, float Radius, Vector3 Color, int FlickerSeed = 0, int CarrierActorId = 0, float Flicker = 1f, Vector3 Wander = default);
