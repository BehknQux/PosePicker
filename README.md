# Pose Picker

A Unity editor tool for making an Animator state hold a hand-shaped pose taken from any frame of an animation clip, plus a small runtime component that plays it.

- Pick a clip and a frame; preview it on your character in the Scene view.
- Turn the whole body around the hips, rotate single bones with gizmos, or drag hands and feet with editor-only two-bone IK (the result is stored as bone rotations; no IK runs in game).
- Optionally let the pose lean toward an aim direction your code sets at runtime.
- Draw how the body blends into the pose (duration and a curve, with presets) and preview the blend from another clip.
- Save poses by name as `PoseAsset`s and load them back.
- **Apply** writes it all onto an Animator state as a `PoseOffset` behaviour; `ModelPose` plays it at runtime.

Humanoid rigs only.

## Install

Package Manager → **+** → **Install package from git URL…**:

```
https://github.com/BehknQux/PosePicker.git#v1.0.0
```

or add to `Packages/manifest.json`:

```json
"com.behknqux.posepicker": "https://github.com/BehknQux/PosePicker.git#v1.0.0"
```

Requires Unity 6 (6000.0) or newer.

## Use

1. Add `ModelPose` to the character's root object (the object above the one with the `Animator`).
2. Open **Tools → Pose Picker**, pick the model, its Animator Controller, a state and a clip.
3. Pick the frame, shape the pose, set the blend.
4. Press **Apply**. The state now plays the clip frozen on that frame (speed 0, Cycle Offset), turns the clip's Loop Time on (Unity ignores Cycle Offset on non-looping clips) and sets transitions into the state to 0 s, since the pose's own blend replaces them. Enter the state from code with `Animator.Play` for the same reason.

Saved poses go to the folder shown in the window's Library section (default `Assets/Poses`). The setting is stored in `ProjectSettings/PosePickerSettings.asset`, so it is shared through version control.

## Runtime API

| Member | What it does |
|---|---|
| `ModelPose.Lean` | Extra rotation around the hips, used by states with **Follow aim** on. Set it from your code (e.g. a dive tilting along its flight). |
| `ModelPose.enabled = false` | Hand the model to something else (e.g. a ragdoll). `ModelPose` writes nothing while disabled, and both disabling and enabling drop the current pose and `Lean`, so it starts plain again. |
| `PoseOffset` | The `StateMachineBehaviour` that **Apply** puts on a state. Rotation, bones, follow aim, blend-in time and curve. |
| `PoseAsset` | A saved pose (clip, frame and everything above). Used by the window only. |

`ModelPose` reads only the local `Animator`, so in a networked game every machine can run it from what it shows with nothing sent over the network.

## License

MIT, see [LICENSE.md](LICENSE.md).
