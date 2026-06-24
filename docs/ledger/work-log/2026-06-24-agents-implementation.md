# AGENTS.md Implementation Work Log

**Purpose:** Record the creation of the root `AGENTS.md` contract for future agents working in this repository.

**Status:** Complete

**Scope:** Agent workflow guidance, repository constraints, verification policy, and ledger usage instructions

## Summary

Added a root `AGENTS.md` that gives future Codex/LLM agents a concise, workflow-aware contract for working in `dream`.

## Contract Established

- Defines the repo as a `.NET 8` OpenTK/BulletSharp desktop project
- Tells agents what to read first before editing
- Establishes a recommended order of operations for non-trivial work
- Captures repo-specific cautions such as hardcoded Windows asset paths and warning-bearing builds
- Defines minimum verification expectations
- Explains when to use each ledger folder

## Verification

- Verified that `AGENTS.md` exists at the repository root
- Verified that `docs/` and `docs/ledger/` exist
- Verified the references used by `AGENTS.md`
- Did not run `dotnet build dream\dream.csproj` because this change only added documentation files
