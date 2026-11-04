using System;
using System.Collections.Generic;
using UnityEngine;

namespace PosePicker
{
    /// <summary>
    /// Sits on an Animator state (set by the Pose Picker window) and shapes the pose while that state
    /// plays: Rotation turns the whole body (a held pose's own angle, e.g. how a dive pose lies in the
    /// air), Bones turn single bones on top of the clip (an arm opened wider, the head raised),
    /// FollowAim lets an action's aim lean the body (e.g. a dive), and BlendIn / BlendShape shape how the
    /// body moves into the pose. ModelPose applies all of it.
    /// Local on every machine like the rest of the animation: no packets.
    /// </summary>
    public class PoseOffset : StateMachineBehaviour
    {
        [Serializable]
        public struct BoneOffset
        {
            public HumanBodyBones Bone;
            [Tooltip("Turn added to the bone's animated local rotation.")]
            public Quaternion Rotation;
        }

        [Tooltip("Degrees: X tips the head down, Y turns, Z rolls.")]
        public Vector3 Rotation;

        public List<BoneOffset> Bones = new();

        [Tooltip("The body leans to the aim (an action's direction) on top of the pose.")]
        public bool FollowAim;

        [Tooltip("Seconds the body takes to move from where it was into this pose. 0: at once.")]
        [Min(0f)] public float BlendIn = 0.1f;

        [Tooltip("How the move into the pose runs over BlendIn: across is time (0..1), up is how far into the pose " +
                 "(0 where the body was, 1 the pose; above 1 overshoots).")]
        public AnimationCurve BlendShape = AnimationCurve.Linear(0f, 0f, 1f, 1f);

        public override void OnStateEnter(Animator animator, AnimatorStateInfo stateInfo, int layerIndex)
        {
            ModelPose pose = animator.GetComponentInParent<ModelPose>();
            if (pose != null)
                pose.SetState(this);
        }

        public override void OnStateExit(Animator animator, AnimatorStateInfo stateInfo, int layerIndex)
        {
            ModelPose pose = animator.GetComponentInParent<ModelPose>();
            if (pose != null)
                pose.ClearState(this);
        }
    }

    public static class BlendShapes
    {
        /// <summary>Progress 0..1 in, weight out (may pass 1 for an overshoot). No curve: even pace.</summary>
        public static float Evaluate(AnimationCurve shape, float t)
        {
            t = Mathf.Clamp01(t);
            return shape != null && shape.length > 0 ? shape.Evaluate(t) : t;
        }

        /// <summary>Progress 0..1 in, what is left of the pose out: 1 down to 0, easing at both ends.</summary>
        public static float FadeOut(float t) => 1f - Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t));
    }
}
