# Design sheet: <creature>

Reference images: <paths/links>     Pose: <crouch / stand / sit>     Style: realistic | stylized
Size (m): body length <..>  back height <..>  overall height <..>
Measured ratios (side view): back height / body length = <..>; ear (horn) / body length = <..>; head length / body length = <..>
Budget: <tris> game triangles, <tex>² maps, bones <n>

Palette (sample lit mid-tones):
  fur mid / dark / light : (r,g,b) / (r,g,b) / (r,g,b)
  underside              : (r,g,b)
  back tint              : (r,g,b)
  accents (nose, inner ear, claws, eye) : ...

| Region tag | Primitives | Bone | Flow | Color / roughness notes |
|---|---|---|---|---|
| back | torso, rump | Spine/Hips | backward-down | darker, cooler |
| ... | | | | |

Special parts: eyes (radius, axis), whiskers (count, length), tail (fluff amp)
Animations: Idle (breath, ears, sniff), Alert, ...
Acceptance: four-view sheet matches the reference silhouette; close-ups of head, paws and tail; animations show no tearing.
