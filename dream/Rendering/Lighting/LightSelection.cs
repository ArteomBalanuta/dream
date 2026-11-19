using Dream.Rendering.Core;
using OpenTK.Mathematics;

namespace Dream.Rendering.Lighting;

// Picks the point lights the world pass shades with this frame: the LightList.MaxLights lights
// nearest the camera whose sphere reaches into the view frustum. Pure and allocation free: the
// result is kept sorted in a fixed LightList.MaxLights-slot insertion buffer on the stack, so no
// sort and no list are needed. Lights at the same distance keep their input order (a later light
// never displaces an earlier one at equal distance), so a stable scene never flickers between
// equally near lights.
public static class LightSelection
{
    // Selects from one span. The list is overwritten: Count, then PositionRadius and Color for
    // slots [0, Count), nearest first. Slots past Count are left as they were.
    public static void SelectNearest(ReadOnlySpan<PointLight> lights, Vector3 camera, in ViewFrustum frustum, ref LightList list)
    {
        SelectNearest(lights, ReadOnlySpan<PointLight>.Empty, camera, in frustum, ref list);
    }

    // Selects from two spans as if they were concatenated (`first` then `second`). Render uses it
    // to add the DREAM_DEBUG_POINT_LIGHTS ring to the scene's lights without copying them.
    //
    // carriers, when given, receives each kept light's CarrierActorId at the same list index (a held light's carrier
    // is the actor holding it, 0 for every other light; the fire shadow pass reads it to keep the carrier out of its
    // own light's shadow).
    public static void SelectNearest(
        ReadOnlySpan<PointLight> first,
        ReadOnlySpan<PointLight> second,
        Vector3 camera,
        in ViewFrustum frustum,
        ref LightList list,
        Span<int> carriers = default)
    {
        Span<float> distances = stackalloc float[LightList.MaxLights];
        int count = 0;
        Insert(first, camera, in frustum, ref list, distances, ref count, carriers);
        Insert(second, camera, in frustum, ref list, distances, ref count, carriers);
        list.Count = count;
    }

    // The view-model range: the same lights with their positions moved into eye space, because the
    // view model draws with view = identity and its fragPos is in eye space. OpenTK uses row
    // vectors, so GLSL's `view * vec4(p, 1)` is TransformPosition(p, view) here (as in
    // FrameLightingBuffer). The view matrix is rigid, so radius and color are kept.
    public static void ToEyeSpace(in LightList world, in Matrix4 view, ref LightList viewModel)
    {
        int count = Math.Clamp(world.Count, 0, LightList.MaxLights);
        viewModel.Count = count;
        for (int i = 0; i < count; i++)
        {
            Vector4 positionRadius = world.PositionRadius[i];
            Vector3 eye = Vector3.TransformPosition(positionRadius.Xyz, view);
            viewModel.PositionRadius[i] = new Vector4(eye, positionRadius.W);
            viewModel.Color[i] = world.Color[i];
        }
    }

    private static void Insert(
        ReadOnlySpan<PointLight> lights,
        Vector3 camera,
        in ViewFrustum frustum,
        ref LightList list,
        Span<float> distances,
        ref int count,
        Span<int> carriers)
    {
        bool carryCarriers = carriers.Length >= LightList.MaxLights;
        for (int i = 0; i < lights.Length; i++)
        {
            ref readonly PointLight light = ref lights[i];
            if (!frustum.IntersectsBox(light.Position, new Vector3(light.Radius)))
            {
                continue;
            }

            float distance = Vector3.DistanceSquared(light.Position, camera);
            if (!float.IsFinite(distance))
            {
                continue;
            }

            // Full and no nearer than the farthest kept light: equal distance keeps the earlier one.
            if (count == LightList.MaxLights && distance >= distances[count - 1])
            {
                continue;
            }

            // First slot whose distance is strictly greater: ties stay behind earlier lights.
            int slot = count < LightList.MaxLights ? count : LightList.MaxLights - 1;
            while (slot > 0 && distances[slot - 1] > distance)
            {
                slot--;
            }

            int last = count < LightList.MaxLights ? count : LightList.MaxLights - 1;
            for (int j = last; j > slot; j--)
            {
                distances[j] = distances[j - 1];
                list.PositionRadius[j] = list.PositionRadius[j - 1];
                list.Color[j] = list.Color[j - 1];
                if (carryCarriers)
                {
                    carriers[j] = carriers[j - 1];
                }
            }

            distances[slot] = distance;
            list.PositionRadius[slot] = new Vector4(light.Position, light.Radius);
            list.Color[slot] = new Vector4(light.Color, 0f);
            if (carryCarriers)
            {
                carriers[slot] = light.CarrierActorId;
            }

            if (count < LightList.MaxLights)
            {
                count++;
            }
        }
    }
}
