Purpose: Record the intake and repo integration of a curated CC0 asset library for future gameplay and rendering work.
Status: Completed
Scope: Quaternius character and animal packs, Kenney nature and retro character packs, Poly Haven terrain textures, and the repo documentation updates that describe the new library.

# Asset Library Intake

## Summary

The repository now includes a curated asset vault under `dream/animations/library/` for future model, creature, and terrain work.

Downloaded sources include:

- Quaternius animal and character packs
- Kenney `nature-kit` and `animated-characters-retro`
- Poly Haven terrain textures

The new content is already covered by the existing `dream.csproj` runtime copy rules because it lives under `dream/animations/**`.

The asset locations documentation and repo README were updated so future work can find the new library without rediscovering the folder layout.

## Verification

- Confirmed the new files live under `dream/animations/library/`
- Confirmed `dream\dream.csproj` already copies `animations\**\*` into output
- Updated the documentation references for the new source folders
