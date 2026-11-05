#version 330 core

// The shadow caster fragment stage: depth only, so there is nothing to write (the FBO has no colour attachment).
// Paired with cubeVertex.glsl verbatim (skinning, instancing and wind sway), see Render.Shaders.cs.
//
// The MASK variant (Shader.AlphaMaskVariant, compiled with WORLD_ALPHA_MASK) discards every fragment: the only MASK
// primitives in the world are the flora/landmark contact discs (fake ambient occlusion) and the decal-layer ground
// quads, and neither may cast. If a genuine alpha-cutout caster is ever authored, this becomes a real alpha test.
#ifdef WORLD_ALPHA_MASK
void main() { discard; }
#else
void main() { }
#endif
