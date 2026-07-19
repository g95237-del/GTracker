# Unity Runtime Framework Research

This document records the runtime animation and event systems considered for generic EDI discovery. The goal is to expose stable names and timing without modifying game assets, scanning every loaded object each frame, or guessing gameplay meaning.

## Support Policy

Framework presence and framework activity are different signals. GTracker reports curated assembly/type evidence as `CAPABILITY` telemetry, but capability rows are never mapping candidates and never trigger EDI playback. A framework becomes trigger-eligible only after an observer can produce a stable candidate and lifecycle.

Priority order:

1. Stable Unity engine APIs that work in Mono and IL2CPP.
2. Public framework events or one validated managed transition method.
3. Cached reflection for optional frameworks with version probing.
4. Game-specific patches when no generic semantic boundary exists.

GTracker does not inject `AnimationEvent` entries, add `StateMachineBehaviour` instances, patch broad methods such as `Object.Instantiate`, or replace callbacks on game-owned tween objects.

## Framework Matrix

| System | Runtime evidence | Generic candidate quality | Current policy |
| --- | --- | --- | --- |
| Unity Animator (Mecanim) | `UnityEngine.AnimationModule.dll`, `Animator` | Clip/state transitions and effective timing | Observed |
| Unity Legacy Animation | `Animation`, `AnimationState` | Clip name, weight, speed, wrap mode, timing | Observed |
| PlayableDirector / Timeline | `UnityEngine.DirectorModule.dll`, `PlayableDirector` | Playable asset name, time, duration, wrap mode | Observed |
| PlayMaker | `PlayMaker.dll`, `Fsm.SwitchState` | FSM and state names | Observed |
| Unity Visual Scripting / Bolt | `Unity.VisualScripting.*.dll` | Named custom events and state transitions | Detected; adapter planned |
| Spine-Unity | `spine-unity.dll` or `Spine.Unity.SkeletonAnimation` | Animation name, track, phase, loop events | Observed on Mono; detected on IL2CPP |
| Live2D Cubism | `Live2D.Cubism*` or `CubismMotionController` | Motion name, duration, speed, weight | Detected; next typed adapter |
| Animancer | `Kybernetik.Animancer` / `AnimancerComponent` | Active state key/clip and timing | Detected; versioned adapter planned |
| DragonBones | `UnityArmatureComponent` | State name, play count, time and completion | Detected; reflection adapter planned |
| Fungus | `Fungus.Flowchart` | Flowchart, block, command and menu lifecycle | Detected; event adapter planned |
| Yarn Spinner | `Yarn.Unity.DialogueRunner` | Dialogue and node lifecycle | Detected; event adapter planned |
| Adventure Creator | `AC.EventManager` | Action-list, conversation and speech lifecycle | Detected; version probing required |
| Pixel Crushers Dialogue System | `DialogueSystemController` | Conversation and line lifecycle | Detected; event adapter planned |
| Naninovel | `Naninovel.Engine` | Script, track and command lifecycle | Detected; version probing required |
| NodeCanvas | `FSMOwner` | FSM and dialogue transitions | Detected; version probing required |
| Behavior Designer | `BehaviorTree` | Tree lifecycle; task-level data is noisy | Detected only |
| Ink | `Ink.Runtime.Story` | Story navigation if a story instance is found | Detected only; no global registry |
| DOTween | `DG.Tweening.DOTween` | Usually timing without a semantic name | Detected only |
| LeanTween | `LeanTween` | No efficient semantic enumerate-all API | Detected only |
| Sprite swapping | `SpriteRenderer.sprite`, `Image.sprite` | Frame name only, no authored state/timing | Opt-in diagnostic only |
| Standalone PlayableGraph | Owner-specific graph handle | Clip data when a graph owner is known | Observe through known owners only |

## Implemented Observers

### Legacy Animation

The generated observer discovers active `Animation` components once per reconciliation scan and polls only cached components. It chooses the highest-weight enabled `AnimationState`, resolves the clip/state name, applies state speed to duration, resolves component/clip wrap mode, and emits `LEGACY_ANIMATION` lifecycle events. It does not use `Animation.clip`, which is only the component's default clip.

References:

- <https://docs.unity3d.com/ScriptReference/Animation.html>
- <https://docs.unity3d.com/ScriptReference/AnimationState.html>

### PlayableDirector / Timeline

The generated observer uses only `UnityEngine.Playables.PlayableDirector`, avoiding a compile-time dependency on the separately versioned `Unity.Timeline.dll` package. It reports the playable asset name, owner path, current time, duration, play state, and wrap mode. Custom tracks remain opaque, but their containing playable asset is still discoverable.

References:

- <https://docs.unity3d.com/ScriptReference/Playables.PlayableDirector.html>
- <https://docs.unity3d.com/Packages/com.unity.timeline@1.8/api/UnityEngine.Timeline.TimelineAsset.html>

### PlayMaker

PlayMaker uses an exact Harmony postfix for `Fsm.SwitchState(FsmState)` and a one-second current-state reconciliation fallback. This avoids polling every FSM at render rate while retaining initial-state and patch-failure visibility.

Reference:

- <https://hutonggames.fogbugz.com/default.asp?W1>

### Spine-Unity

For Mono games, the generated observer discovers active `SkeletonAnimation` components and polls their cached `AnimationState.Tracks`. Each visible active track reports its public animation name, track index, animation range, combined component/state/entry time scale, phase, duration, and loop lifecycle as `SPINE_ANIMATION` events. The track is appended to the telemetry object path so explicit mappings cannot collide across tracks. Empty, delayed, zero-alpha, completed, and removed tracks end cleanly. `SkeletonGraphic` is not observed yet, and IL2CPP Spine remains detected-only until generated interop API compatibility is validated.

References:

- <https://esotericsoftware.com/spine-unity-main-components#SkeletonAnimation-Component>
- <https://esotericsoftware.com/spine-api-reference#TrackEntry>

## Planned Adapters

Live2D is the highest-value next adapter for 2D character games because it exposes semantic motion names, duration, phase, and completion. Its generated code must be conditional on exact assemblies or IL2CPP interop wrappers.

Visual Scripting, Fungus, Yarn Spinner, and dialogue frameworks are event sources rather than animation clocks. Their candidates should use separate event kinds while continuing to map through the existing explicit runtime mapping policy. Broad generic event buses are intentionally avoided because they are noisy and can carry arbitrary objects.

Tween engines are not animation-state frameworks. DOTween IDs are optional, enumerate-all APIs allocate, and callback setters mutate game-owned callbacks. Detection is useful diagnostic evidence, but automatic trigger support requires a game-specific stable tween ID or target.

## Runtime Constraints

- Unity object access remains on the main thread.
- Generated source includes optional static types only when the matching reference is detected.
- IL2CPP adapters require generated interop assemblies and may be unavailable when stripping removes the needed API.
- Runtime polling is bounded: Animator, Legacy Animation, and Mono Spine at 30 Hz, PlayableDirector at 10 Hz, and component reconciliation at 1 Hz during discovery.
- Identical states do not produce repeated Play requests. Loop telemetry is limited to one row per stream per second.
- Explicit project mappings are required before discovery-only framework candidates can drive EDI.
- Exact UTC cycle capture assumes the animation clock advances at its reported rate. Non-unit global time scale, manual Timeline clocks, reverse Legacy playback, and PingPong playback may require a game-specific timing adapter.

## Primary Sources

- Unity Playables: <https://docs.unity3d.com/ScriptReference/Playables.PlayableGraph.html>
- Spine events: <https://esotericsoftware.com/spine-unity-events>
- Live2D motions: <https://docs.live2d.com/en/cubism-sdk-manual/motion-unity/>
- Animancer state API: <https://kybernetik.com.au/animancer/api/Animancer/AnimancerState/>
- DragonBones C# runtime: <https://github.com/DragonBones/DragonBonesCSharp>
- Unity Visual Scripting custom events: <https://docs.unity3d.com/Packages/com.unity.visualscripting@1.9/api/Unity.VisualScripting.CustomEvent.html>
- Fungus: <https://github.com/snozbot/fungus>
- Yarn Spinner Unity: <https://github.com/YarnSpinnerTool/YarnSpinner-Unity>
- Ink Unity integration: <https://github.com/inkle/ink-unity-integration>
- DOTween API: <https://dotween.demigiant.com/documentation.php>
