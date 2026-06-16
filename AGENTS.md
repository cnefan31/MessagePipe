# AGENTS.md

This file provides guidance to the AI agent when working with code in this repository.

## Build & Test

```bash
# PostBuildUtility must be built first (used by MessagePipe's post-build step)
dotnet build ./tools/PostBuildUtility/ -c Release
# Build everything
dotnet build -c Release
# Run tests
dotnet test -c Release --no-build
```

Tests for `MessagePipe.Redis.Tests` and `MessagePipe.Nats.Tests` require Redis and NATS services. Use `docker-compose up` to start them.

## Unity Source Sync

Do NOT edit files under `src/MessagePipe.Unity/` directly. They are auto-copied from `src/MessagePipe/` by the PostBuild target in `MessagePipe.csproj`. Edit the source in `src/MessagePipe/` and the Unity copy is updated on build. The PostBuild step also runs `tools/PostBuildUtility` to apply Unity-specific code transformations (e.g., replacing DI APIs with Unity equivalents).

## Code Generation

`src/MessagePipe/Disposables.cs` is generated from `Disposables.tt` (T4 template). Edit the `.tt` file, not the `.cs` output.

## Conditional Compilation

Source files use `#if UNITY_2018_3_OR_NEWER` / `#if !UNITY_2018_3_OR_NEWER` to branch between .NET and Unity code paths. The same source files compile for both targets.

## Style

- Warnings are treated as errors (`WarningsAsErrors`)
- Nullable reference types are enabled project-wide
- C# files use 4-space indent; all other files use 2-space indent
- C# files use UTF-8 with BOM

## Branch

Default branch is `master`.
