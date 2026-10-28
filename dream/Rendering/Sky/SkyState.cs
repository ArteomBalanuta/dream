using Dream.Rendering.Lighting;
using OpenTK.Mathematics;

namespace Dream.Rendering.Sky;

public struct SkyState
{
    public FrameLighting Lighting;      // CameraPosition.xyz left 0; the uploader fills it
    public Vector3 ZenithColor;
    public Vector3 HorizonColor;        // always == Lighting.FogColor.Xyz
    public Vector3 SunDirection;        // unit, may be below the horizon
    public Vector3 SunDiscColor;        // 0 when the sun is below the horizon
    public float SunDiscAngularRadius;  // radians
    public Vector3 MoonDirection;       // unit
    public Vector3 MoonDiscColor;
    public float MoonDiscAngularRadius;
    public float StarVisibility;        // 0..1
    public float TimeOfDay01;           // wrapped input
}
