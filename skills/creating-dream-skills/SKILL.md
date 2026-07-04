---
name: creating-dream-skills
description: Creates and refines repo-local agent skills for the Dream C# game project. Use when adding a new folder under G:\workspace\projects\dream\skills, updating a Dream-specific SKILL.md, designing skill metadata, adding one-level references or scripts, or validating that a skill follows concise progressive-disclosure best practices.
---

# Creating Dream Skills

## Workflow

1. Read `AGENTS.md`, `README.md`, and `docs/ledger/README.md`.
2. Inspect existing folders under `skills/` before naming a new skill.
3. Choose a lowercase hyphenated name under 64 characters. Prefer gerund/action names such as `profiling-dream-multiplayer`.
4. Keep `SKILL.md` concise. Put only trigger-critical facts in frontmatter and only task-critical procedure in the body.
5. Add `agents/openai.yaml` with a short display name, short description, and a default prompt that explicitly mentions `$skill-name`.
6. Add resources only when they pay for themselves:
   - `scripts/` for deterministic or fragile repeatable operations.
   - `references/` for one-level deep details loaded only when needed.
   - `assets/` for files copied into outputs, not for process notes.
7. Validate the skill, then report paths and exactly what was checked.

## Dream Conventions

- Place repo-local skills in `G:\workspace\projects\dream\skills`.
- Do not add README, changelog, installation guide, or other auxiliary files inside a skill folder.
- Prefer factual Dream repo seams over generic advice. Link agents to `docs/configuration.md`, `docs/performance-and-optimizations.md`, or ledger files when the skill depends on them.
- If a skill changes project workflow knowledge, add or update a ledger work-log entry outside the skill.
- Keep references one level deep from `SKILL.md`; do not create nested reference chains.

## Validation

Use the system skill validator when available:

```powershell
python 'C:\Users\arteo\.codex\skills\.system\skill-creator\scripts\quick_validate.py' 'G:\workspace\projects\dream\skills\<skill-name>'
```

If that path is unavailable, manually verify:

- `SKILL.md` exists.
- YAML frontmatter has only `name` and `description`.
- `name` matches the folder and uses lowercase letters, digits, and hyphens only.
- `description` says what the skill does and when to use it.
- `agents/openai.yaml` strings are quoted and `default_prompt` includes `$skill-name`.
