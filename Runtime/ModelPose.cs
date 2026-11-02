using System.Collections.Generic;
using UnityEngine;

namespace PosePicker
{
    /// <summary>
    /// The one writer of the body model's pose on top of the Animator. Each frame, after the Animator
    /// posed the bones, it turns single bones by the playing state's PoseOffset.Bones, then turns the
    /// whole model around its hips by the state's PoseOffset.Rotation and, if the state follows the aim,
    /// by Lean on top (an action's angle, e.g. an attack tilting to its aim). Nothing else rotates the
    /// model while it is enabled.
    /// Entering a state with a PoseOffset, the body moves into the new pose from where it stood over
    /// BlendIn seconds along the state's BlendShape (a PoseSnapshot taken at entry, blended toward the
    /// live pose). Code entering such a state plays it at once (no Animator cross-fade on top).
    /// While something else owns the model (a ragdoll), disable this component: it writes nothing then,
    /// and disabling or enabling it drops the state and Lean, so the pose starts plain again.
    /// It reads only the local Animator, so in a networked game every machine can run it from what it
    /// shows, with no packets.
    /// </summary>
    public class ModelPose : MonoBehaviour
    {
        private Animator _animator;
        private Transform _model;
        private Transform _hips;
        private Vector3 _baseLocalPosition;
        private Quaternion _baseLocalRotation;
        private PoseOffset _state;
        private PoseSnapshot _snapshot;
        private float _blendStart, _blendDuration;
        private AnimationCurve _blendShape;

        /// <summary>An action's extra turn of the body, around the hips, used by states that follow the aim.</summary>
        public Quaternion Lean { get; set; } = Quaternion.identity;

        private void Awake()
        {
            _animator = GetComponentInChildren<Animator>();
            _model = _animator.transform;
            _hips = _animator.isHuman ? _animator.GetBoneTransform(HumanBodyBones.Hips) : null;
            _baseLocalPosition = _model.localPosition;
            _baseLocalRotation = _model.localRotation;
            _snapshot = new PoseSnapshot(_animator);
        }

        /// <summary>
        /// Called on entering the state, inside the Animator's update: the bones still hold last frame's
        /// final pose, which is what the blend starts from.
        /// </summary>
        public void SetState(PoseOffset state)
        {
            if (!isActiveAndEnabled)
                return;
            _state = state;
            _blendDuration = state.BlendIn;
            _blendShape = state.BlendShape;
            if (_blendDuration > 0f)
            {
                _snapshot.Capture();
                _blendStart = Time.time;
            }
        }

        /// <summary>Only the state that set it may clear it: during a cross-fade the next one enters first.</summary>
        public void ClearState(PoseOffset state)
        {
            if (_state == state)
                _state = null;
        }

        // While disabled the Animator may be off too and call no OnStateExit; forget everything on both
        // edges so the pose starts plain when the model is ours again.
        private void OnEnable() => Forget();
        private void OnDisable() => Forget();

        private void Forget()
        {
            Lean = Quaternion.identity;
            _state = null;
            _blendDuration = 0f;
        }

        // LateUpdate: after the Animator posed the bones this frame, so the offsets go on top of the clip.
        private void LateUpdate()
        {
            Quaternion rotation = Quaternion.identity;
            if (_state != null)
            {
                ApplyBones(_animator, _state.Bones);
                rotation = Quaternion.Euler(_state.Rotation);
                if (_state.FollowAim)
                    rotation = Lean * rotation;
            }
            Apply(_model, _hips, _baseLocalPosition, _baseLocalRotation, rotation);

            if (_blendDuration > 0f)
            {
                float t = (Time.time - _blendStart) / _blendDuration;
                if (t >= 1f)
                    _blendDuration = 0f;
                else
                    _snapshot.BlendTo(BlendShapes.Evaluate(_blendShape, t));
            }
        }

        /// <summary>
        /// Turns each listed bone by its offset, on top of the rotation the Animator gave it this frame.
        /// Shared with the Pose Picker window's preview.
        /// </summary>
        public static void ApplyBones(Animator animator, List<PoseOffset.BoneOffset> bones)
        {
            if (bones == null || !animator.isHuman)
                return;
            foreach (PoseOffset.BoneOffset offset in bones)
            {
                Transform bone = animator.GetBoneTransform(offset.Bone);
                if (bone != null)
                    bone.localRotation *= offset.Rotation;
            }
        }

        /// <summary>
        /// Puts the model at its base pose turned by rotation around the hips (in the parent's space).
        /// The hips are read in the model's own space, so the model's current turn does not move them.
        /// Shared with the Pose Picker window's preview.
        /// </summary>
        public static void Apply(Transform model, Transform hips, Vector3 baseLocalPosition,
            Quaternion baseLocalRotation, Quaternion rotation)
        {
            Vector3 hipsInModel = hips != null ? model.InverseTransformPoint(hips.position) : Vector3.up;
            Vector3 pivot = baseLocalPosition + baseLocalRotation * hipsInModel;
            model.localRotation = rotation * baseLocalRotation;
            model.localPosition = pivot + rotation * (baseLocalPosition - pivot);
        }
    }
}
