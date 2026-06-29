Purpose: Capture the oversized wolf-model issue and define a reusable runtime normalization approach for imported world assets.
Status: Complete
Scope: World-space asset sizing metadata, authored-size compensation, and the first normalization seam for creature assets

# Summary

After the wolf became visible again, the next issue was not rendering correctness but asset scale. The imported wolf is much larger than intended for the current world and player proportions.

The problem is not unique to the wolf. It is a general imported-asset integration concern: authored size and in-game size are not the same thing, and the runtime needs a stable way to express that.

# Evidence

Temporary inspection of `dream/animations/enemies/wolf.glb` showed:

- mesh node world scale approximately `100x`
- effective authored world bounds:
  - min `(-0.495, 0.003, -1.474)`
  - max `(0.495, 3.194, 2.415)`
- effective authored size:
  - width `0.990`
  - height `3.191`
  - depth `3.889`

That means the asset is currently authored at roughly `3.19` world units tall before any additional scene-side scale tuning.

The current code still uses a direct `WolfAssetDefinition.VisualScale` constant. That is not expressive enough because it hides the more important question:

- what size is the asset authored at?
- what size should it be in the game world?

# Root Cause

The runtime has no explicit world-asset normalization seam. Imported assets can only be corrected through ad hoc scale constants, which makes the code brittle and hard to extend when more creatures or props arrive.

# Recommended Approach

Use explicit normalization metadata for world assets.

For the current project shape, the smallest clean seam is:

- declare the asset's authored height
- declare the target in-game height
- derive the final uniform render scale from those values

This keeps gameplay code asset-agnostic and makes sizing decisions legible in metadata instead of hiding them inside transform math.

# Recommendation

Introduce a reusable `WorldAssetNormalization` helper and migrate the wolf to:

- `AuthoredHeight = 3.191f`
- `TargetHeight = 1.20f`

That yields a derived render scale of approximately `0.376`, which should bring the wolf much closer to the current player/world proportions while keeping the solution reusable for future imported world assets.
