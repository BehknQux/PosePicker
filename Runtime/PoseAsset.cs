using System;
using System.Collections.Generic;
using UnityEngine;

namespace PosePicker
{
    /// <summary>
    /// A named pose saved by the Pose Picker window: a frame of a clip plus the body rotation, bone
    /// turns, aim following, blend-in and bone mask made on it.
    /// Two uses:
    /// - A library to keep and reload poses. A whole-body pose reaches the game through the window's
    ///   Apply, as an Animator state's PoseOffset.
    /// - A layered pose (Mask has branches) is played straight from this asset by code:
    ///   ModelPose.PlayLayered(pose). The masked bones take Rotations, the rest keeps whatever the
    ///   Animator plays (e.g. legs running under a held upper body).
    /// Rotations is written on Save: every bone's local rotation at the picked frame with the bone turns
    /// on, read on the model the window previewed. It is that body's pose; another humanoid with a
    /// different rest pose would want its own save.
    /// </summary>
    [CreateAssetMenu(menuName = "Pose Picker/Pose", fileName = "Pose")]
    public class PoseAsset : ScriptableObject
    {
        [Serializable]
        public struct BoneRotation
        {
            public HumanBodyBones Bone;
            [Tooltip("The bone's local rotation in the pose.")]
            public Quaternion Rotation;
        }

        public AnimationClip Clip;
        [Tooltip("Seconds into the clip.")]
        public float Time;
        [Tooltip("Degrees, the whole body around the hips. Whole-body poses only.")]
        public Vector3 Rotation;
        public List<PoseOffset.BoneOffset> Bones = new();
        [Tooltip("Whole-body poses only.")]
        public bool FollowAim;
        [Min(0f)] public float BlendIn = 0.1f;
        public AnimationCurve BlendShape = AnimationCurve.Linear(0f, 0f, 1f, 1f);

        [Tooltip("Which bones take the pose. No branch: the whole body (an Animator state holds it).")]
        public BoneMask Mask = new();
        [Tooltip("Layered poses: seconds the bones take to go back to the animation underneath.")]
        [Min(0f)] public float BlendOut = 0.2f;
        [Tooltip("Written on Save: each bone's local rotation in the finished pose.")]
        public List<BoneRotation> Rotations = new();

        public bool IsLayered => Mask != null && !Mask.IsWholeBody;
    }
}
