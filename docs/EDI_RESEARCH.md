# EDI Research Notes

Research date: 2026-07-15.

The implementation targets EDI source commit [`d55fd09`](https://github.com/NoGRo/Edi/commit/d55fd09b5a7be48bf7ac09018d5289956a93f354), dated 2026-05-01, plus publicly distributed integrations and EroScripts authoring guidance. The current source identifies the project as 1.0.2; public integration packages may bundle older EDI builds.

## Verified Authoring Contract

The conservative interoperable gallery layout is:

```text
Gallery/
├── Definitions.csv
├── scene.funscript
├── scene.twist.funscript
└── intense/
    ├── another_scene.funscript
    └── another_scene.twist.funscript
```

`Definitions.csv` columns:

```csv
Name,FileName,StartTime,EndTime,Type,Loop,Description
```

- `Name` is the case-insensitive API action key.
- `FileName` is the extensionless script stem.
- Start/end are absolute source-script timestamps. Integer milliseconds are least ambiguous.
- Implemented types are `gallery`, `reaction`, and `filler`.
- Only trimmed, case-folded `true` enables looping.
- Description is optional in older packages but emitted by this studio.
- Duplicate names are rejected case-insensitively.
- Manual `Definitions.csv` takes precedence over generated `Definitions_auto.csv`.

Primary implementation: [DefinitionRepository.cs](https://github.com/NoGRo/Edi/blob/d55fd09b5a7be48bf7ac09018d5289956a93f354/Edi.Core/Gallery/Definition/DefinitionRepository.cs).

EDI consumes top-level legacy Funscript actions:

```json
{
  "version": "1.0",
  "inverted": false,
  "range": 100,
  "actions": [
    { "at": 0, "pos": 50 },
    { "at": 1000, "pos": 100 }
  ]
}
```

For compatibility, exported `at`, `pos`, and metadata duration values must be integer JSON tokens. Current OFS 4 can emit floating metadata duration such as `0.0`; EDI's integer metadata model rejects this and can misleadingly report that no actions were found. See [NoGRo/Edi issue 12](https://github.com/NoGRo/Edi/issues/12).

EDI ignores Funscript 1.1 combined `axes` and Funscript 2.0 `channels`. Additional axes should be separate files. Verified suffixes are Default (unsuffixed), Surge, Sway, Twist, Roll, Pitch, Vibrate, Valve, Suction, Rotate. There is no `Linear` axis enum; unsuffixed Default represents the primary stroke axis.

Folder variants are safer than multi-dot filenames, especially when axes are present. Discovery scans the gallery root and one immediate child folder. Deeper folders are not discovered.

## Loop And Segment Behavior

- EDI slices source actions inclusively between start and end.
- It subtracts the definition start to make segment actions relative.
- Adjacent definitions sharing a boundary both include the boundary action.
- Loop repair can insert/snap boundary actions and make the final position equal the first.
- Explicit points at both boundaries avoid ambiguous repair and shared-source side effects.

Primary implementation: [FunscriptRepository.cs](https://github.com/NoGRo/Edi/blob/d55fd09b5a7be48bf7ac09018d5289956a93f354/Edi.Core/Gallery/Funscript/FunscriptRepository.cs).

Gallery semantics:

- `gallery`: normal primary playback.
- `reaction`: interrupts primary playback and returns to it afterward.
- `filler`: fallback when normal playback stops.

## HTTP API

Current EDI binds HTTP to `127.0.0.1:5000`; optional HTTPS adds port 5001. Swagger is normally at `http://localhost:5000/swagger/index.html`.

Core routes used by integrations:

| Method | Route | Purpose |
|---|---|---|
| POST | `/Edi/Play/{encodedName}?seek=0` | Start or seek a definition |
| POST | `/Edi/Stop` | Stop selected channels |
| POST | `/Edi/Pause?untilResume=true` | Hard-pause output |
| POST | `/Edi/Resume?AtCurrentTime=false` | Resume from stored seek |
| POST | `/Edi/Intensity/{0..100}` | Set relative maximum |
| GET | `/Edi/Definitions` | Read active definitions |
| GET | `/Edi/Channels` | Read channel names |

Channels can be supplied as `?channels=player1,player2` or a `channels` header. Query values take precedence.

Primary implementation: [EdiController.cs](https://github.com/NoGRo/Edi/blob/d55fd09b5a7be48bf7ac09018d5289956a93f354/Edi.Core/Controllers/EdiController.cs).

New integrations should use POST. Some historical builds and hidden current routes also accept state-changing GET requests, but relying on those routes is unnecessary and unsafe.

## Bundles

Current source consumes `BundleDefinition.txt` and `BundleDefinition.<variant>.txt` in the gallery root or one immediate child folder. It does not use the stale documented AppData `BundleDefinition.csv` location.

```text
-Combat
attack
hit

-default
filler
```

- Headings start with `-` at column zero.
- Trimmed `#` lines are comments.
- Gallery membership comparisons are case-sensitive.
- Generated bundle assets are stored under `%LOCALAPPDATA%\Edi\Bundles`.

Primary implementations: [IndexRepository.cs](https://github.com/NoGRo/Edi/blob/d55fd09b5a7be48bf7ac09018d5289956a93f354/Edi.Core/Gallery/Index/IndexRepository.cs) and [GalleryBundler.cs](https://github.com/NoGRo/Edi/blob/d55fd09b5a7be48bf7ac09018d5289956a93f354/Edi.Core/Gallery/Index/GalleryBundler.cs).

## Unity Integration Patterns

The common architecture is:

```text
Unity callbacks / narrow Harmony patches
    -> semantic state reducer
    -> one ordered HTTP queue
    -> EDI loopback API
```

Recommended practices derived from real integrations:

- Patch semantic scene/animation methods instead of scanning all objects each frame.
- Keep Unity and IL2CPP object access on Unity's main thread.
- Pass only immutable strings/numbers to an HTTP worker.
- Reuse one HTTP client with a short timeout.
- Percent-encode action names.
- Serialize requests so `Play`, `Pause`, `Resume`, and `Stop` cannot reorder.
- Debounce repeated callbacks; do not send one request per animation frame.
- Treat EDI as optional and fail open without breaking gameplay.
- Track pause reasons as a set so focus return cannot resume a still-open game menu.
- For actual game pause use EDI hard pause, then default resume when Unity resumes.
- For variable animation speed, prepare speed-bucket actions and periodically re-seek at meaningful transitions rather than continuously restarting playback.

Unity Mono usually exposes `<Game>_Data/Managed/Assembly-CSharp.dll` and commonly uses BepInEx 5 `BaseUnityPlugin`. IL2CPP usually exposes `GameAssembly.dll` plus `il2cpp_data/Metadata/global-metadata.dat` and commonly uses BepInEx 6 IL2CPP `BasePlugin` with generated interop assemblies.

Relevant references:

- [BepInEx Unity Mono installation](https://docs.bepinex.dev/master/articles/user_guide/installation/unity_mono.html)
- [BepInEx Unity IL2CPP installation](https://docs.bepinex.dev/master/articles/user_guide/installation/unity_il2cpp.html)
- [BepInEx Harmony patching](https://docs.bepinex.dev/master/articles/dev_guide/runtime_patching.html)
- [Il2CppInterop class injection](https://github.com/BepInEx/Il2CppInterop/blob/master/Documentation/Class-Injection.md)
- [LoveMachine](https://codeberg.org/Sauceke/LoveMachine), a GPL architectural reference supporting Mono and IL2CPP but controlling Intiface directly rather than EDI
- [Mage Kanade EDI integration topic](https://discuss.eroscripts.com/t/110822), a public IL2CPP integration example
- [Alien Quest EVE EDI integration](https://discuss.eroscripts.com/t/240206), a distributed Mono/BepInEx integration
- [Guilty Hell EDI integration](https://discuss.eroscripts.com/t/290181), a Mono/Harmony integration
- [Succubus Tower EDI integration](https://discuss.eroscripts.com/t/312607), a distributed IL2CPP integration
- [To4st integration mods](https://discuss.eroscripts.com/t/99000), examples of animation-phase and speed-bucket synchronization

## Existing Tool Landscape

No verified existing tool combines live rolling capture, immediate scene extraction, action labeling, editable funscript output, canonical EDI export, and Unity Mono/IL2CPP scaffolding.

Adjacent tools:

- [OpenFunscripter](https://github.com/Eroscripts/OFS): mature video-based scripting; current metadata requires EDI compatibility care.
- [ofs-ng](https://github.com/ofs69/ofs-ng): active GPL multi-axis editor.
- [F8Studio](https://github.com/feel8-fun/f8studio): AGPL/commercial live CV tooling, not an EDI authoring pipeline.
- [LoveMachine](https://codeberg.org/Sauceke/LoveMachine): GPL Unity integration architecture, not EDI packaging.
- [CubiLink](https://discuss.eroscripts.com/t/305584): binary Unity animation/multi-axis tooling, not a public EDI exporter.
- [EDI definition helper topic](https://discuss.eroscripts.com/t/118261): simple definition generation, not integrated capture/mod authoring.
- [Sunblock EDI Community Mod](https://github.com/Sunblock-Code/EDI-Mod): community EDI fork with additional editing/conversion work.

This studio reimplements the needed interoperation boundary rather than copying code with incompatible or ambiguous licensing.

## Documentation Drift

Several forum/README statements differ from current source:

| Older documentation | Current verified behavior |
|---|---|
| `Definition.csv` | `Definitions.csv` |
| `definition_auto.csv` | `Definitions_auto.csv` |
| Filename tags can be in any order | Parser requires loop tag before type tag |
| `.linear` is an axis | It is interpreted as a variant; Default is unsuffixed |
| `name.variant.axis.funscript` is reliable | Folder variant plus axis suffix is safer |
| AppData `BundleDefinition.csv` | Gallery-local `BundleDefinition*.txt` |
| All current routes accept GET and POST | Public current mutation controller uses POST |
| OFS failure is caused by property order | Floating integer-model fields cause deserialization failure |

Forum guidance remains useful context, but exporter behavior is based on current source and inspected packages:

- [Official EDI authoring guide](https://discuss.eroscripts.com/t/118446)
- [Official EDI software topic](https://discuss.eroscripts.com/t/108186)

## Security And Licensing Notes

- Keep EDI bound to loopback. Current API has no authentication and permissive CORS.
- Avoid automatic use of `/Edi/Assets`; current source accepts client filenames and has path-handling concerns. This studio writes galleries on disk and uses only definitions/play/stop for tests.
- EDI's repository has no root license file. `Edi.Core.csproj` declares an MIT package license, but that is not a clear repository-wide grant. Interoperate through documented files and HTTP rather than copying EDI source.
- F8Studio is AGPL-3.0/commercial, LoveMachine is GPL-3.0, and several authoring tools are closed or binary-only. Concepts may inform design, but code reuse requires license review.
