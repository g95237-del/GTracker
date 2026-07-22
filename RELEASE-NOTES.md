# EDI Integration Studio v0.1.0-alpha.1

First public alpha of GTracker / EDI Integration Studio, a Windows authoring tool for building EDI game integrations.

## Highlights

- Rolling DXGI capture with review, trim, and multi-axis funscript authoring.
- Timed Capture Cycle workflow for supported Unity Animator, Legacy Animation, Timeline, and Mono Spine events.
- Unity runtime analysis, packaged BepInEx provisioning, generated mod projects, and owned plugin installation.
- Runtime telemetry correlation and explicit animation/path/duration mappings.
- Validated EDI Gallery export with automatic filler scripts.

## Install

1. Download `GTracker-v0.1.0-alpha.1-win-x64.zip`.
2. Extract the entire archive to a writable folder.
3. Run `GTracker.App.exe` without moving it away from the accompanying DLLs and `RuntimePackages` folder.

The ZIP is self-contained for Windows x64, so launching the Studio does not require a separate .NET runtime. The .NET 8 SDK and applicable targeting packs are still required when generating and building Unity integration plugins.

## Alpha Warning

This is experimental software. Back up projects, expect game-specific compatibility issues, and review the privacy notes before sharing captures or generated files. Support availability is not guaranteed.

See the [README](https://github.com/g95237-del/GTracker#readme) and [wiki](https://github.com/g95237-del/GTracker/wiki) for requirements and workflows.
