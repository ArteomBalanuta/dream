# Viewmodel Rig Text Config

Date: 2026-06-28
Status: Drafted and reviewed
Owner: Codex

## Summary

Move the current hardcoded viewmodel rig defaults out of code-only startup state and into a dedicated text configuration file that is:

- easy to edit by hand
- auto-created if missing
- resilient to invalid lines
- loaded once at startup
- isolated from unrelated runtime settings

This first pass should load from the text file at startup, but runtime menu edits should remain in-memory only for the current session.

## Goals

- Make viewmodel tuning defaults editable without recompiling.
- Keep the implementation small and focused rather than introducing a generic config framework.
- Follow the project's existing runtime asset-path model.
- Preserve a safe startup path when the file is missing or partially invalid.
- Keep `Render.cs` and `PlayScene.cs` free of parsing and file-format logic.

## Non-Goals

- Converting all `GameSettings` into text-backed configuration.
- Auto-saving runtime slider changes back into the file.
- Adding a full preset management system.
- Introducing JSON, YAML, or reflection-driven config loading.

## Design Overview

The project should load viewmodel rig defaults through a dedicated text config file instead of encoding them directly inside `ViewModelRigSettings`.

The first-pass load model is:

1. construct built-in default `ViewModelRigSettings`
2. resolve the runtime config file path
3. create the file from defaults if it is missing
4. read and parse the file
5. override only recognized values
6. fall back per-field on missing or invalid values
7. pass the final settings object into `GameSettings`

This keeps startup robust while making the values externally editable.

## File Location

The config file should live in a runtime-owned config folder under project content:

- source path: `dream/config/viewmodel-rig.txt`
- runtime output path: `dream/bin/<configuration>/<tfm>/config/viewmodel-rig.txt`

This matches the current asset model used for `shaders/`, `animations/`, and `fonts/`, and keeps the file project-visible rather than hidden in a user profile directory.

## Text Format

The file format should be flat key-value text with one entry per line:

```txt
body_anchor_spread=0.000
body_anchor_drop=0.306
body_anchor_forward_pull=0.180
held_item_position_x=-0.046
held_item_position_y=0.204
held_item_position_z=-0.156
held_item_rotation_x=0.938
held_item_rotation_y=1.406
held_item_rotation_z=-1.406
```

The first pass should cover the values currently being moved out of hardcoded defaults. The format should tolerate:

- blank lines
- leading or trailing whitespace
- comment lines beginning with `#`

Unknown keys should be ignored.

## Architecture

### Runtime Model

`ViewModelRigSettings` remains the in-memory runtime model used by:

- `GameSettings`
- `PlayerViewModelController`
- `ViewModelPoseResolver`
- the runtime menu in `Render`

It should no longer be the sole place where startup defaults are invented.

### Loader / Writer

Add a focused helper such as `ViewModelRigTextConfig` with responsibilities to:

- create default file content
- parse text into a `ViewModelRigSettings`
- write a config file from a settings instance
- load a settings instance from disk with per-field fallback

This helper should be the only place that understands the text format.

### Path Resolution

Extend `AssetPaths.cs` with a config-path helper so runtime config resolution uses the same application-base-directory approach as the rest of the project.

Example shape:

- `AssetPaths.Config("viewmodel-rig.txt")`

### Startup Wiring

`Game.cs` should bootstrap the config before `PlayScene` is created.

Recommended flow:

1. load `ViewModelRigSettings` through `ViewModelRigTextConfig`
2. pass the loaded rig into `GameSettings`
3. construct `PlayScene` with the configured `GameSettings`

That keeps startup ownership in the shell layer rather than in rendering or scene logic.

## Failure Handling

### Missing File

If the file is missing:

- create it from built-in defaults
- then load it normally

This should not block startup.

### Invalid Value

If a recognized key has an invalid numeric value:

- keep the built-in default for that field
- continue parsing the rest of the file

This should not block startup.

### Unknown Key

If a key is not recognized:

- ignore it

This allows the format to evolve without making older builds fragile.

## Runtime Behavior

For the first pass, the text file is a startup default source only.

That means:

- the file is read once at startup
- runtime menu sliders continue editing the in-memory settings
- `Reset Viewmodel Tuning` resets to the in-memory default model used by the current run
- runtime edits are not automatically written back to disk

This keeps the first version predictable and avoids silent file mutation.

## Testing

Add tests for:

- missing file creates a new config file from defaults
- valid file values override built-in defaults
- invalid numeric values fall back per-field
- unknown keys are ignored
- blank lines and comment lines are tolerated
- startup wiring uses loaded values instead of code-only defaults

Tests should focus on the config helper and `GameSettings` startup seam, not on renderer behavior.

## Documentation Updates

Update:

- `docs/configuration.md`
- `docs/build-and-run.md`

to explain:

- where `viewmodel-rig.txt` lives
- that it is copied into runtime output
- that it is auto-created if missing
- that runtime menu edits are not yet auto-saved

## Risks And Mitigations

### Risk: Hardcoded defaults split across two places

Mitigation:
Keep one canonical built-in default builder and let the text writer serialize from that source.

### Risk: Config parsing leaks into unrelated systems

Mitigation:
Concentrate all file-format and parsing behavior in `ViewModelRigTextConfig`.

### Risk: Silent confusion about runtime edits not persisting

Mitigation:
Document clearly that this first pass is startup-load only, not live save-back.

## Follow-Up Opportunities

- add explicit `Save Viewmodel Config` from the runtime menu
- expand the file to include shared rig and hands values as needed
- generalize the pattern later if other runtime settings benefit from text-backed startup defaults
