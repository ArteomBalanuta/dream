#version 330 core
out vec4 FragColor;
uniform vec3 color;

// The crosshair. Its pure red (luma 0.21) all but vanishes against the dark blue night sky and night
// terrain, so the colour is lifted toward white by CrosshairLift: same hue, about twice the luma,
// still clearly red against the bright midday sky.
const float CrosshairLift = 0.3;

void main()
{
    FragColor = vec4(mix(color, vec3(1.0), CrosshairLift), 1.0);
}
