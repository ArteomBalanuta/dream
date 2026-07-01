# Explicit Executable Apphost Build Output

Purpose: record the project configuration update that makes executable output explicit.
Status: completed
Scope: project build configuration, build/run documentation, regression coverage

## Summary

- Added explicit `UseAppHost=true` to `dream/dream.csproj`.
- Kept `OutputType=Exe` as the project output kind.
- Added `ProjectBuildConfigurationTests` so the executable/apphost settings stay guarded.
- Updated README and build/configuration docs to clarify that Windows builds emit `dream\bin\Debug\net10.0\dream.exe`.

## Notes

The .NET SDK build summary can still print `dream.dll` because that is the managed assembly behind the apphost. The runnable Windows artifact is `dream.exe`, and the build output now declares apphost generation explicitly.
