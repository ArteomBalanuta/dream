# Work log: asset generation and capture skills

- Purpose: record two new repo-local agent skills.
- Status: done, on branch `docs/asset-skills` from `integrate/ground-assets` db41fba1.
- Scope: documentation only; no code or asset changed.

## What landed

- `skills/generating-dream-assets/`: how to add or change a procedural asset end to end with `tools/ui-assets`:
  which generator and kit per family, the 3D export conventions, the `dream.ground/1` manifest and the
  add-a-ground-asset checklist (including the C# count of 100 in `GroundScatterLayoutTests`), the check_sources,
  shipped-bytes, determinism, budget and palette gates, art direction, and a symptom-to-fix table.
- `skills/capturing-dream-screenshots/`: the capture rules from `docs/configuration.md` ("Capture traps") as a
  procedure, plus `scripts/capture.ps1`, which waits until no `dream.exe` runs, launches one auto-connecting capture,
  kills only its own process on timeout, and prints the PNG names, the stderr tail and the session lines of stdout.

## Findings worth keeping

- A ground generator run with `--only` writes only the named GLBs and leaves `<family>.json` untouched (fixed in
  ground loop 1; it used to truncate the manifest). Iterate into a scratch root; regenerate with a full run.
- `gen_ground_decals.TILE_LUMA` mirrors the terrain tiles in `dream/animations/library/forge`; retuning a terrain
  tile makes `check_ground_decals.py` red until it is updated.
