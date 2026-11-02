using System.Collections.Generic;
using UnityEngine;

namespace PosePicker
{
    /// <summary>
    /// A named pose saved by the Pose Picker window: a frame of a clip plus the body rotation, bone
    /// turns, aim following and blend-in made on it. A library to keep and reload poses; ModelPose does
    /// not read it yet (a state that should hold a pose gets it through the window's Apply, as a
    /// PoseOffset).
    /// </summary>
    [CreateAssetMenu(menuName = "Pose Picker/Pose", fileName = "Pose")]
    public class PoseAsset : ScriptableObject
    {
        public AnimationClip Clip;
        [Tooltip("Seconds into the clip.")]
        public float Time;
        [Tooltip("Degrees, the whole body around the hips.")]
        public Vector3 Rotation;
        public List<PoseOffset.BoneOffset> Bones = new();
        public bool FollowAim;
        [Min(0f)] public float BlendIn = 0.1f;
        public AnimationCurve BlendShape = AnimationCurve.Linear(0f, 0f, 1f, 1f);
    }
}
