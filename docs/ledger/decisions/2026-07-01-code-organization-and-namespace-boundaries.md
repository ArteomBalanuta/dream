Purpose: define durable source organization rules for Dream after the client/server multiplayer split
Status: accepted
Scope: C# namespaces, source folders, dependency direction, and future project split guidance

## Decision

Dream uses `Dream` as the root namespace and organizes production source by bounded context folders under the existing `dream.csproj`.

The first migration keeps one project file. A later multi-project split may happen only after folder and namespace boundaries are stable and dependency direction tests are green.

## Namespace Rules

- folder path maps to namespace path
- production namespaces start with `Dream`
- test namespaces start with `Dream.Tests`
- server code must not depend on client or rendering namespaces
- protocol code must not depend on client presentation, OpenGL, ImGui, or OpenTK windowing
- explicit local `using` directives are preferred over broad global aliases

## Rationale

The flat source root obscured the now-real client, server, protocol, simulation, and rendering boundaries. Folder and namespace boundaries make ownership clearer without forcing an immediate assembly split.
