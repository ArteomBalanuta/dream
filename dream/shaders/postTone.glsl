// postTone.glsl - `#include "postTone.glsl"` from the post chain's fragment shaders (one level, no
// #version). The single source of truth for where this engine's highlight shoulder begins and what
// it does, shared by the bright pass and the resolve so the two can never disagree: the bloom takes
// exactly the light the shoulder is about to compress, and nothing else.
//
// WHY A SHOULDER AND NOT A TONEMAP. Until this pass existed the world drew straight to the default
// framebuffer, so every channel was hard-clipped at 1.0. That is why the art is calibrated to an
// albedo ceiling of 0.42: at the peak gain of about 2.2 beside a brazier, anything brighter landed
// on a flat white patch with no shape in it. A normal filmic tonemap would fix the clipping and
// also move every value already in the picture, invalidating years of tuning, the golden baselines
// and the character palette gates in one commit.
//
// So this curve is the IDENTITY below the knee. A pixel under 0.80 comes out bit-for-bit as it went
// in, which is almost the whole frame; above it the curve bends over and asymptotes to 1.0, so a
// spark, a torch-lit pauldron or an arcane gem keeps its shape instead of becoming a white blob.
// The shoulder is C1 continuous - its slope at the knee is exactly 1 - so there is no seam or band
// where it takes over. Mirrored by PostSettings.Knee and pinned by a test.
const float DreamToneKnee = 0.80;

// How much of a channel's distance above the knee survives. 1 - knee, so the curve reaches 1.0 only
// at infinity: nothing clips, ever.
const float DreamToneHead = 1.0 - DreamToneKnee;

// The rolloff, applied to the MAX CHANNEL and then scaled into the colour rather than per channel.
// Per-channel compression desaturates a highlight toward white, which is right for film and wrong
// here: this art direction is built on coloured light - a brazier's orange, an arcane violet - and
// a highlight that slides to white loses the one thing that says which light made it.
vec3 dreamToneRolloff(vec3 c)
{
    float m = max(max(c.r, c.g), c.b);
    if (m <= DreamToneKnee)
    {
        return c;
    }

    float t = m - DreamToneKnee;
    float mapped = DreamToneKnee + (DreamToneHead * (t / (t + DreamToneHead)));
    return c * (mapped / m);
}

// What the shoulder is about to take away: the part of a pixel that sits above the knee, in the
// pixel's own colour. A pixel below the knee returns exactly zero, which is the property the whole
// bloom rests on - a dark frame cannot bloom, so the glow can never wash the shadows out. It is a
// threshold, not a weight, so the bright pass has no tuning knob of its own to drift from this one.
vec3 dreamToneOverflow(vec3 c)
{
    float m = max(max(c.r, c.g), c.b);
    if (m <= DreamToneKnee)
    {
        return vec3(0.0);
    }

    return c * ((m - DreamToneKnee) / m);
}
