#version 330 core

// One half of a separable Gaussian over the quarter-resolution bloom texture. Run twice, across
// then down, through a ping-pong pair; uDirection is (texel, 0) then (0, texel).
//
// Five taps, not nine: the weights below are a sigma-2 Gaussian evaluated at the three LINEAR tap
// offsets that stand in for a 9-tap kernel, so every fetch is a bilinear pair and the whole blur
// costs six fetches for the reach of eighteen. At a quarter resolution that is a glow about 20
// source pixels wide, which is the scale a torch halo wants and cheap enough to stay off the
// frame budget.
in vec2 vUv;
out vec4 FragColor;

uniform sampler2D uSource;
uniform vec2 uDirection;

const float Weight0 = 0.2270270270;
const float Weight1 = 0.3162162162;
const float Weight2 = 0.0702702703;
const float Offset1 = 1.3846153846;
const float Offset2 = 3.2307692308;

void main()
{
    vec3 sum = texture(uSource, vUv).rgb * Weight0;
    sum += texture(uSource, vUv + uDirection * Offset1).rgb * Weight1;
    sum += texture(uSource, vUv - uDirection * Offset1).rgb * Weight1;
    sum += texture(uSource, vUv + uDirection * Offset2).rgb * Weight2;
    sum += texture(uSource, vUv - uDirection * Offset2).rgb * Weight2;
    FragColor = vec4(sum, 1.0);
}
