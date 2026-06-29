Purpose: Document why the first wolf enemy remains invisible despite existing actor, combat, and debug-geometry support.
Status: Complete
Scope: Animated glTF world-model rendering for the wolf asset, including transform composition and skinning support

# Summary

The wolf actor exists in scene runtime and can be targeted by combat, but its visual model is not rendered in the expected world location. Investigation shows two distinct issues:

1. the current node/world transform composition in `AnimatedModel.RenderPrepared()` is incorrect for assets whose mesh node carries its own scale or translation
2. the render pipeline reads `JOINTS_0` and `WEIGHTS_0` data but never applies a skinning palette in the active shader path

The first issue explains the current invisibility. The second explains why the wolf asset is still not fully supported even after the visibility fix.

# Evidence

## Runtime evidence

- The wolf can damage the player and receive damage, proving the enemy actor exists in the scene runtime.
- The collision-debug toggle draws a red debug marker at the expected enemy world position, proving scene placement is reasonable.
- The visible model still does not appear at that location.

## Asset evidence

Temporary inspection of `dream/animations/enemies/wolf.glb` showed:

- `Scenes=1`
- `Meshes=1`
- `Nodes=68`
- `Skins=1`
- `Animations=24`

The asset contains:

- one mesh node named `Cube`
- one logical skin with `49` joints
- mesh primitives that expose both `JOINTS_0` and `WEIGHTS_0`

The mesh node sits outside the armature hierarchy and carries its own local transform:

- translation approximately `(0, 0, 0.062)`
- scale approximately `(100, 100, 100)`

## Code-path evidence

Current render composition in `AnimatedModel.RenderPrepared()`:

```csharp
var nodeWorldMatrix = node.WorldMatrix.ToOpenTK();
var combinedMatrix = modelMatrix * nodeWorldMatrix;
```

For a wolf actor model matrix with world translation `(5.5, 0, 2.5)`, the above composition produces a combined translation approximately:

- `(550, 0, 250.06)`

That pushes the model far outside the intended combat/debug position and explains the present invisibility.

Reversing the composition to:

```csharp
var combinedMatrix = nodeWorldMatrix * modelMatrix;
```

preserves the actor-space placement and yields translation approximately:

- `(5.5, 0, 2.55)`

## Shader evidence

`dream/shaders/cubeVertex.glsl` currently uses:

- position
- normal
- texcoord

but ignores:

- joint indices
- joint weights

`AnimatedModel.ProcessVertices()` already reads `JOINTS_0` and `WEIGHTS_0`, so the loader is ahead of the shader/runtime support. This is the current asset-support gap.

# Root Cause

## Immediate root cause of invisibility

The world-instance transform and the glTF node-world transform are composed in the wrong order for the repo's current matrix conventions. Any asset with non-identity node scale or translation can be displaced dramatically.

## Broader support gap

The renderer does not yet upload or apply glTF skinning matrices, so skinned creature assets are only partially supported. Even once visible, the wolf will not animate correctly until first-pass skinning is added.

# Recommended Fix Shape

1. Introduce a small pure transform-composition helper and switch `AnimatedModel.RenderPrepared()` to the correct matrix order.
2. Add a focused first-pass glTF skinning path:
   - detect logical skins during model initialization
   - build a joint palette from SharpGLTF joint world matrices and inverse bind matrices
   - upload the palette through the existing shader path
   - update `cubeVertex.glsl` to blend skinned positions and normals when a primitive uses joint weights
3. Document the first-pass scope explicitly:
   - one logical skin per asset is supported cleanly now
   - broader multi-skin / richer material support can follow later if needed

# Risks

- Matrix-order changes affect all model-backed rendering, not only the wolf. Regression checks should include viewmodels and cubes.
- The first-pass skinning path should stay bounded. This fix is about making current skinned assets render and animate, not about building a full animation system overhaul.

# Recommendation

Implement the transform-order correction and first-pass skinning support together. The transform fix solves the current invisibility bug, while skinning closes the most important missing asset-support gap for creature work already underway.
