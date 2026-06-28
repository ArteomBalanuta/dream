# Scene Event Foundation

Purpose: record the introduction of the first scene-scoped event hub.
Status: completed
Scope: typed publish/subscribe foundation and scene ownership seam

## Summary

Added a small typed `SceneEventHub` and threaded it into `PlayScene` as scene-local infrastructure for later decoupling work.

## Completed Work

- added typed publish/subscribe support
- added explicit subscription disposal behavior
- added focused tests for dispatch order and unsubscribe behavior
- established `PlayScene` as the local owner of event lifetime

## Verification

- `dotnet build dream\\dream.csproj --nologo`
- `dotnet test dream.Tests\\dream.Tests.csproj --filter "FullyQualifiedName~SceneEventHubTests" --nologo`
