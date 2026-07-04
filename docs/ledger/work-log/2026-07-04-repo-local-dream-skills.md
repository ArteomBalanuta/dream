# Repo-Local Dream Skills

Purpose: Record the first project-owned agent skills added under `skills/`.
Status: Completed
Scope: Skill authoring workflow and multiplayer profiling workflow.

## Summary

Added two repo-local skills:

- `skills/creating-dream-skills/` for creating and validating future Dream-specific skills.
- `skills/profiling-dream-multiplayer/` for launching a dedicated server plus clients with performance and network diagnostics enabled.

The profiling skill includes `scripts/launch-and-summarize.ps1`, which can build or reuse the current executable, launch a server, launch configurable auto-connected clients, sample window FPS and process state, summarize diagnostics logs, and clean up launched processes unless `-KeepRunning` is requested.

## Verification

- Fallback structural validation checked both `SKILL.md` files for required frontmatter, matching skill names, valid name format, and description length.
- `skills/profiling-dream-multiplayer/scripts/launch-and-summarize.ps1` was smoke-tested with `-DurationSeconds 1 -ClientCount 0 -SkipBuild`; it launched the server, wrote logs under `logs/perf-run-*`, summarized diagnostics, and stopped the server.

## Notes

The official `quick_validate.py` script could not run in the active Python environment because `PyYAML` was missing. The skills were still created with the official scaffold first, then checked with a built-in fallback validator.
