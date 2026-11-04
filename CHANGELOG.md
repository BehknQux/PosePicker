# Changelog

## [1.1.0] - 2026-11-04
- Layered poses (bone mask), like Unreal's Layered blend per bone: `BoneMask` branches with blend depth and exclude, set in the window's new Mask section.
- `ModelPose.PlayLayered` / `StopLayered` / `RefreshMask` play a masked `PoseAsset` over the Animator; `PoseLayer` applies it.
- `PoseAsset` gains `Mask`, `BlendOut` and baked bone `Rotations` (written on Save).
- Window preview: an animation plays underneath with the pose held or blending in and out.

## [1.0.0] - 2026-11-02
- First release as a package: Pose Picker window, PoseOffset, ModelPose, PoseSnapshot, PoseAsset.
- The pose save folder is a project setting (ProjectSettings/PosePickerSettings.asset).
- ModelPose no longer knows any ragdoll: disable it while something else owns the model.
