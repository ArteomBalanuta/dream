using Dream.Rendering.Lighting;
using OpenTK.Mathematics;

namespace Dream.Rendering.Sky;

public static class SkyModel
{
    public static SkyState Evaluate(float timeOfDay01) => Evaluate(timeOfDay01, SkyModelSettings.Default);

    public static SkyState Evaluate(float timeOfDay01, in SkyModelSettings settings)
    {
        FrameLighting lighting = FrameLighting.ReferenceNoon; // C0 stub: fixed noon
        Vector3 sun = lighting.SunDirection.Xyz;
        return new SkyState
        {
            Lighting = lighting,
            ZenithColor = new Vector3(0.25f, 0.45f, 0.85f),
            HorizonColor = lighting.FogColor.Xyz,
            SunDirection = sun,
            SunDiscColor = new Vector3(1f, 0.95f, 0.85f),
            SunDiscAngularRadius = 0.03f,
            MoonDirection = -sun,
            MoonDiscColor = Vector3.Zero,
            MoonDiscAngularRadius = 0.025f,
            StarVisibility = 0f,
            TimeOfDay01 = timeOfDay01 - MathF.Floor(timeOfDay01),
        };
    }
}
