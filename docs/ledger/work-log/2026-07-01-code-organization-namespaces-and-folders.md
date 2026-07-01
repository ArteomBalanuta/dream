Purpose: summarize the source-layout migration from a flat C# root to bounded context folders and namespaces
Status: completed
Scope: production source folders, test folders, namespace conventions, dependency direction tests

## Completed

- Added `Dream` and `Dream.Tests` namespace conventions.
- Moved source files from the flat `dream/` root into bounded context folders.
- Mirrored production organization in the test project.
- Added source-layout, namespace-convention, and dependency-direction tests.
- Updated project documentation to describe the new layout.

## Verification

- `dotnet test dream.Tests\dream.Tests.csproj --nologo /p:UseSharedCompilation=false`
- `dotnet build dream\dream.csproj --nologo /p:UseSharedCompilation=false`
