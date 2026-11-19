# Developer hygiene tools

Run from the repository root. Everything they write goes under `artifacts/` (gitignored).

## worktrees.ps1

```powershell
tools\dev\worktrees.ps1                      # read-only report
tools\dev\worktrees.ps1 -CleanBuildOutput    # delete build output of other worktrees
tools\dev\worktrees.ps1 -RemoveMerged [-Yes] # git worktree remove for merged, clean, unlocked worktrees
```

With no flags it changes nothing. Per worktree it prints the size of `dream/bin`, `dream/obj`, `dream.Tests/bin`, `dream.Tests/obj` and `artifacts/*-bin`, and the flags `main`, `current`, `merged` (into `-Base`, default `master`), `dirty`, `locked`, `prunable`, `fresh` (a branch with no commit of its own), `running` (a `dream.exe`, `dotnet`, MSBuild node, `VBCSCompiler` or `testhost` process mentions that path), then totals.

- `-CleanBuildOutput` deletes only those build folders, and skips the main worktree, the current one, any `locked` one and any `running` one.
- `-RemoveMerged` runs `git worktree remove` without `--force`, only for worktrees that are merged (into `-Base`, with at least one commit of their own, so a fresh branch is `fresh`, not merged), clean, not locked, not `running` and not main or current. It asks per worktree unless `-Yes`. Git itself refuses when it should.
- It never deletes branches and never uses `git stash`.

A full report over a few hundred worktrees takes about a minute.

## test.ps1

```powershell
tools\dev\test.ps1 -Area Content|Rendering|Simulation|Net|Client|Fast|Full [-Filter x] [-TimeoutMinutes 20] [-AllowConcurrent]
```

Runs `dotnet test dream.Tests\dream.Tests.csproj` with the area's filter (`-Filter` narrows it: a bare word means `FullyQualifiedName~word`). Refuses to start while a `testhost` runs, because concurrent suites give false failures. Streams the output live to the console and `artifacts/test-logs/<area>-<stamp>.log` (the script itself polls both pipes, so nothing is lost at the end and the summary line is always counted), kills only its own process tree on timeout (exit 124), and appends `utc,sha,area,seconds,passed,failed,skipped` to `artifacts/test-durations.csv`, then prints the last five rows for that area. Exit code 3 means it refused to start.

## gallery.py

```powershell
python -I tools\dev\gallery.py            # writes artifacts\gallery\index.html
python -I tools\dev\gallery.py --selftest
```

Standard library only. One page with relative links, grouped: `tools/ui-assets/previews`, `dream/ui/**/*.png`, `tools/ui-assets/iso` and `terrain`, and the newest 40 `artifacts/captures/*/*.png`, each with a thumbnail, its path and its modification time. Open the page from `artifacts/gallery/`, since the links are relative.
