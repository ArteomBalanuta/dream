# Agent capability docs

Date: 2026-11-19

## What changed

- `AGENTS.md`: new "Capabilities At A Glance" table (every tool and repo skill agents can use, with its doc) and
  "Rules Learned The Hard Way" (look before calling a visual change done, albedo ceiling, one suite at a time, no
  `git stash`, worktree reset, derive shared constants, review the combined diff). Verification now asks for a capture
  and the golden run for visual changes.
- `CLAUDE.md`: points the orchestrator at those sections and lists what every subagent brief must carry.
- `.claude/agents/*.md`: each role gets a "Dream specifics" block (read the capability map; role-specific rules such
  as `test.ps1` tiers, regenerating conflicting binaries, review checks drawn from past defects).
- `skills/adding-dream-combat-skills/`: new skill that writes up the Cleave pipeline (content row, `MeleeArcSwing`
  timeline, server swing, `cleave` clip, crescent and blade trail, icon) as the reference for the next skill.

## Why

The tooling sprint (`2026-11-19-tooling-sprint.md`) added capture staging, golden images, test tiers and content
validation, but agents only found them by reading several docs. The map makes them the first thing an agent sees.

## Verified

Documentation only. Every path, file and test named in the new text was checked against the tree; no build or test run.
