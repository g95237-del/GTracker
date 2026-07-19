# EDI Integration Studio

EDI Integration Studio is a Windows authoring tool for creating game integrations for [Easy Device Integration (EDI)](https://github.com/NoGRo/Edi). It replaces the original GTracker visual-motion experiment with a more practical workflow: continuously buffer a visible game window, capture the scene that just happened, review and loop it immediately, draw funscript curves, and export a validated EDI gallery without first assembling a separate video compilation.

The repository and project names remain `GTracker` temporarily; the running application is now branded **EDI Integration Studio**.

## Current Vertical Slice

- Selects a visible game window and captures its client area through DXGI Desktop Duplication.
- Polls capture at 60 Hz and records a memory-bounded JPEG pre-roll at a selectable 20 or 30 FPS target, defaulting to 30 FPS.
- Captures configurable pre-roll and post-roll with `Ctrl+Shift+F8` while the game has focus.
- Reviews, loops, scrubs, marks in/out, and trims a captured scene without leaving the studio.
- Edits 0–100 funscript curves with an OFS-compatible keyboard subset, single-point mouse dragging, and undo/redo.
- Animates a movable, rotatable, resizable linear simulator directly over the captured video.
- Authors all EDI axes as separate scripts: Default, Surge, Sway, Twist, Roll, Pitch, Vibrate, Valve, Suction, and Rotate.
- Stores versioned projects and compressed `.ediclip` review assets using atomic replacement.
- Keeps the ten most recently opened project folders per Windows user and automatically restores the newest valid project on startup.
- Validates names, durations, ranges, duplicate timestamps, loop boundaries, variants, axes, bundles, and export collisions.
- Exports canonical `Definitions.csv`, legacy Funscript 1.0 integer points, separate axis files, folder variants, and optional `BundleDefinition.txt`.
- Uses a managed export manifest so re-export removes stale axes and bundles without touching unrelated files.
- Checks EDI through `GET /Edi/Definitions`, tests selected scenes through `POST /Edi/Play`, and exposes a global `Ctrl+Shift+F12` EDI stop.
- Detects Unity Mono versus IL2CPP, the managed target framework, modular versus monolithic Unity references, BepInEx flavor, and IL2CPP interop readiness.
- Installs the packaged recommended BepInEx 6 x64 Mono or IL2CPP build, launches the selected game until first-run initialization completes, and then closes it.
- Installs a selected fresh EDI instance beside the game while deliberately preserving all destination `Gallery` contents.
- Generates BepInEx Mono or IL2CPP plugins with scene, Animator, Legacy Animation, Timeline, conditional PlayMaker, and Mono Spine discovery, configurable matching presets, scene constants, and an ordered EDI HTTP client.
- Builds and installs only the final owned plugin DLL, then watches structured runtime discovery telemetry inside the studio.
- Refuses to regenerate over an existing mod scaffold, protecting hand-written Harmony patches.

## Requirements

- Windows 10 or later
- x64 GPU with DXGI Desktop Duplication support
- .NET 8 SDK for building
- EDI when testing exported scenes
- A matching BepInEx installation when compiling generated Unity mod source

The studio currently uses `Vortice.Direct3D11 3.8.3` and `OpenCvSharp4 4.13.0.20260627`. It does not control hardware directly; EDI remains responsible for devices, channels, variants, and playback.

## Build And Run

```powershell
dotnet restore GTracker.slnx
dotnet build GTracker.slnx --configuration Release
dotnet test GTracker.slnx --configuration Release
dotnet run --project src\GTracker.App\GTracker.App.csproj --configuration Release
```

## Authoring Workflow

1. Enter a project name, click **New**, and choose a parent folder, or select an existing entry from **Recent projects**. The newest valid recent project opens automatically at startup.
2. Select the game window and click **Start rolling capture**.
3. Play through the game normally. The encoded pre-roll exists only in memory.
4. When a useful scene occurs, press `Ctrl+Shift+F8`. The studio retains the configured pre-roll and waits for the configured post-roll.
5. Scrub or loop the captured scene. Use **Mark in**, **Mark out**, and **Apply trim** to isolate it.
6. When runtime discovery is active, the studio correlates the trimmed clip's UTC interval with Unity scene, Animator, Legacy Animation, PlayableDirector/Timeline, Spine, and supported framework telemetry. A single runtime candidate names the scene and script automatically; ambiguous candidates remain selectable rather than guessed.
7. Confirm the derived scene name and safe script file stem, or use **Use detected name**. Choose `gallery`, `reaction`, or `filler`, loop behavior, variant, and axis.
8. Left-click empty timeline space to add one point, then keep dragging to position that same point. Drag an existing point to move it or right-click near one to remove it.
9. Use the linear simulator over the video to inspect the interpolated script value. Drag its body to move it, its side/corner handles to resize it, and the round handle to rotate it.
10. Click **Preview script** to inspect the exact funscript JSON, exported point count, duration boundary, and clean-loop repair that export will write.
11. Click **Save scene**. The editor state goes into `project.edi.json`; review frames go into `clips/*.ediclip`.
12. Repeat for more game scenes. Use **Validate** before export.
13. Click **Export EDI gallery** and choose the intended collection folder, such as `Gallery\simple` or `Gallery\detailed`. Scripts are written directly into that folder and `Definitions.csv` is written to its `Gallery` parent.
14. Point EDI's `GalleryPath` at the parent `Gallery` folder, reload EDI, then use **Check EDI** and **Test selected**.

## OFS-Compatible Editor Keys

The **OFS keys** button in the timeline shows this list in the application. These defaults follow OpenFunscripter where the studio has the matching capability:

| Keys | Action |
| --- | --- |
| `Space` | Play or pause the review clip |
| `Left` / `Right` | Previous or next captured frame while paused |
| `Ctrl+Left` / `Ctrl+Right` | Step backward or forward six captured frames while paused |
| `Down` / `Up` | Previous or next funscript point |
| `Numpad 0` through `Numpad 9` | Add or edit at positions 0 through 90 |
| `Numpad /` | Add or edit at position 100 |
| `Delete` | Delete the point nearest the playhead |
| `Shift+Arrow` | Nudge the nearest point by one captured frame or one position unit |
| `Ctrl+Shift+Left` / `Ctrl+Shift+Right` | Nudge time and follow the moved point |
| `Ctrl+Z` / `Ctrl+Y` | Undo or redo timeline edits |
| `Numpad -` / `Numpad +` | Decrease or increase playback speed by 0.1x |
| `I` | Invert the nearest point |
| `End` | Move the nearest point to the playhead |
| `Ctrl+S` | Save the project |
| `Ctrl+Shift+S` | Export the EDI gallery |

Text fields and selectors retain normal keyboard input while focused. OFS selection, clipboard, equalize, isolation, and timeline zoom commands are not implemented yet because this editor does not yet have OFS's selection model.

Exported minimum example:

```text
Gallery/
├── .edi-integration-studio-export.json
├── Definitions.csv
└── detailed/
    └── attack.funscript
```

```csv
Name,FileName,StartTime,EndTime,Type,Loop,Description
attack,attack,0,2000,gallery,true,
```

```json
{
  "version": "1.0",
  "inverted": false,
  "range": 100,
  "actions": [
    { "at": 0, "pos": 50 },
    { "at": 1000, "pos": 100 },
    { "at": 2000, "pos": 50 }
  ]
}
```

## Unity Mod Workflow

1. Select the actual game executable rather than a launcher.
2. Choose **Discovery** as the initial preset and click **Analyze Unity runtime**.
3. If setup is incomplete, close the game and click **Install BepInEx + initialize**. Confirm the detected runtime package; the studio verifies and installs it, launches the game, waits for BepInEx (and IL2CPP interop generation when applicable), and closes the game.
4. Optionally click **Install EDI**, select a fresh folder containing `Edi.exe`, and confirm the merge into the game folder. Source `Gallery` files are skipped and an existing destination `Gallery` is left untouched.
5. Click **Generate mod project** and choose an empty folder.
6. Close the game and click **Build + install**. The studio runs `dotnet build`, verifies the output, and installs only `IntegrationMod.dll` under `BepInEx\plugins\<plugin-guid>`.
7. Launch the game and click **Watch discovery** to inspect scene names, Animator and Legacy Animation clips, PlayableDirector/Timeline assets, Mono Spine tracks, PlayMaker FSM states when available, object paths, and transitions in real time. `CAPABILITY` rows report detected framework evidence without becoming mapping candidates, `OBSERVER_SCAN` rows show supported runtime objects, and `OBSERVER_ERROR` identifies a failed probe.
8. Select a discovered scene or supported runtime candidate, select a saved authored scene, and click **Map selected**. Click **Build + install** again to compile that explicit project preset into the plugin; hand-written `Plugin.cs` patches are preserved.
9. If names already correspond to authored scene names, the convention presets can map them without individual entries. Use a narrow game-specific Harmony patch when public scene, Animator, or FSM events are not semantically sufficient.

`Discovery` never triggers EDI automatically unless the project contains an explicit **Map selected** entry. `SceneNames`, `AnimationNames`, and `SceneAndAnimationNames` additionally normalize names to letters and digits and require an exact authored-scene-name match before calling EDI. This avoids substring guesses while still handling names such as `enemy-hit`, `Enemy Hit`, and `enemy_hit` consistently.

The observer can discover loaded scenes, public `Animator` and Legacy `Animation` activity, `PlayableDirector` assets, Mono Spine tracks, and PlayMaker FSM state transitions when available, but it cannot infer arbitrary semantic meanings such as attack, damage, orgasm, or interaction from unknown names. Known frameworks without a validated adapter are reported as detected-only capabilities. Procedural transforms, custom native animation, stripped IL2CPP methods, and obfuscation still need a game-specific adapter. See `docs/UNITY_FRAMEWORK_RESEARCH.md` for the support matrix and rationale.

### Existing Broken Scaffolds

Older generated projects directly referenced DLLs inside the game's `Managed` folder. Visual Studio could then combine Unity's `mscorlib`/`netstandard` facades with the SDK facades and report `CS0731` cycles followed by many `CS0518` predefined-type errors.

Generate a fresh project with the current studio. If preserving an edited old project, remove manually added `UnityEngine.dll`, `mscorlib.dll`, `netstandard.dll`, all game-side `System*.dll` references, and `Assembly-CSharp.dll` unless a real game-specific patch uses it. Set every remaining BepInEx/Unity reference to `Private=false` and `ExternallyResolved=true`, then delete that mod project's `bin` and `obj` directories before rebuilding.

## Safety And Data Handling

- Rolling frames are memory-only until **Save scene** is used.
- The buffer is bounded by time and encoded bytes.
- Project JSON, clips, and export files use temporary files before replacement.
- Scene updates use revisioned clip names so a failed project save cannot corrupt the prior scene.
- Export preserves unrelated Gallery files but refuses any unmanaged file it would replace.
- Generated mod source refuses to overwrite existing files.
- Plugin installation refuses to run while the target game is open and refuses to replace a DLL without the studio's ownership manifest.
- BepInEx installation verifies the packaged archive hash and runtime, blocks while the game is running, rejects unsafe archive paths, and rolls back copied files after a failure.
- EDI installation rejects nested source/destination paths and reparse points, copies files transactionally, and never copies source `Gallery` contents over the destination gallery.
- EDI calls default to loopback `http://127.0.0.1:5000/Edi` and use two-second timeouts.
- Desktop Duplication captures the composed pixels in the window's screen rectangle. Other windows or notifications overlapping the game may appear in the capture.

## Current Boundaries

- Clip review is a JPEG sequence, not a standard MP4/WebM asset. Audio is not captured yet.
- The default recording target is 30 FPS, with a 20 FPS fallback for slower systems. DXGI is polled at 60 Hz, but Desktop Duplication only returns newly presented desktop frames and the bounded encoder may drop stale pending frames under load.
- The selected game must remain visible and non-minimized. Exclusive fullscreen, HDR/10-bit output, rotated displays, protected content, and monitor changes are rejected or require capture restart.
- True reverse game-state scrubbing is not generally possible. The studio loops captured review frames; it does not rewind Unity state.
- Generic runtime discovery cannot recover Animator state names from hashes or determine game semantics; PlayMaker state names help when available, but confirmed mappings or game-specific patches remain necessary.
- The editor has no point selection model, timeline zoom, thumbnail strip, audio track, or automatic curve generation yet.
- One scene currently exports one variant. Multi-variant editing for a shared scene definition needs a dedicated variant-track model.
- Bundles are supported by the project/export model but do not yet have a visual editor.

## Repository Layout

- `src/GTracker.Core/Projects`: versioned authoring model and atomic project store.
- `src/GTracker.Core/Edi`: EDI validator and managed exporter.
- `src/GTracker.Core/Unity`: Unity/BepInEx inspection, source generation, build, and ownership-scoped installation.
- `src/GTracker.App/Capture`: window discovery, DXGI capture, JPEG encoder, and rolling buffer.
- `src/GTracker.App/Controls`: custom funscript timeline and interactive linear simulator overlay.
- `src/GTracker.App/Projects`: compressed review-clip persistence.
- `src/GTracker.App/Edi`: local EDI API test client.
- `tests`: exporter, validation, persistence, Unity tooling, buffer, and clip tests.
- `docs/EDI_RESEARCH.md`: researched EDI behavior, documentation drift, integration patterns, and primary sources.
- `docs/UNITY_FRAMEWORK_RESEARCH.md`: Unity animation/event framework detection, observer priorities, and source references.
