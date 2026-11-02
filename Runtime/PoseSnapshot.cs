using UnityEngine;

namespace PosePicker
{
    /// <summary>
    /// A still copy of a humanoid body's pose: every bone's local rotation, the hips' position and the
    /// model's local place. Blending from it toward the live pose moves the body into a new pose from
    /// wherever it stood, with any curve; Unity's own cross-fade only runs at an even pace.
    /// The source is frozen while blending; over a short blend that is not seen.
    /// </summary>
    public class PoseSnapshot
    {
        private readonly Transform _model;
        private readonly Transform[] _bones = new Transform[(int)HumanBodyBones.LastBone];
        private readonly Quaternion[] _rotations = new Quaternion[(int)HumanBodyBones.LastBone];
        private readonly Transform _hips;
        private Vector3 _hipsPosition;
        private Vector3 _modelPosition;
        private Quaternion _modelRotation;

        public PoseSnapshot(Animator animator)
        {
            _model = animator.transform;
            if (!animator.isHuman)
                return;
            for (int i = 0; i < _bones.Length; i++)
                _bones[i] = animator.GetBoneTransform((HumanBodyBones)i);
            _hips = _bones[(int)HumanBodyBones.Hips];
        }

        /// <summary>Copies the pose the body holds right now.</summary>
        public void Capture()
        {
            for (int i = 0; i < _bones.Length; i++)
                if (_bones[i] != null)
                    _rotations[i] = _bones[i].localRotation;
            if (_hips != null)
                _hipsPosition = _hips.localPosition;
            _modelPosition = _model.localPosition;
            _modelRotation = _model.localRotation;
        }

        /// <summary>
        /// Puts the body between the copy (weight 0) and its current pose (weight 1). Above 1 it goes past
        /// the pose (an overshoot curve), below 0 back past the copy.
        /// </summary>
        public void BlendTo(float weight)
        {
            if (Mathf.Approximately(weight, 1f))
                return;
            for (int i = 0; i < _bones.Length; i++)
                if (_bones[i] != null)
                    _bones[i].localRotation = Quaternion.SlerpUnclamped(_rotations[i], _bones[i].localRotation, weight);
            if (_hips != null)
                _hips.localPosition = Vector3.LerpUnclamped(_hipsPosition, _hips.localPosition, weight);
            _model.localPosition = Vector3.LerpUnclamped(_modelPosition, _model.localPosition, weight);
            _model.localRotation = Quaternion.SlerpUnclamped(_modelRotation, _model.localRotation, weight);
        }
    }
}
