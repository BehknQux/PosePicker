using UnityEngine;

namespace PosePicker
{
    /// <summary>
    /// One layered pose ready to put on a body: per bone, how strongly (the mask) and toward which local
    /// rotation (the pose). Built once when the pose starts, then applied each frame on top of whatever
    /// the Animator posed. Shared by ModelPose and the Pose Picker window's preview.
    /// </summary>
    public class PoseLayer
    {
        public readonly PoseAsset Pose;
        private readonly Transform[] _bones = new Transform[(int)HumanBodyBones.LastBone];
        private readonly Quaternion[] _rotations = new Quaternion[(int)HumanBodyBones.LastBone];
        private float[] _weights;
        private readonly Animator _animator;

        public PoseLayer(Animator animator, PoseAsset pose)
        {
            _animator = animator;
            Pose = pose;
            if (!animator.isHuman)
            {
                _weights = new float[_bones.Length];
                return;
            }
            for (int i = 0; i < _bones.Length; i++)
                _bones[i] = animator.GetBoneTransform((HumanBodyBones)i);
            SetRotations(pose);
            RefreshMask();
        }

        /// <summary>For the window: a layer from rotations it has just read, not a saved asset's.</summary>
        public PoseLayer(Animator animator, BoneMask mask, Quaternion[] rotations)
        {
            _animator = animator;
            for (int i = 0; i < _bones.Length; i++)
            {
                _bones[i] = animator.isHuman ? animator.GetBoneTransform((HumanBodyBones)i) : null;
                _rotations[i] = rotations[i];
            }
            _weights = mask.Weights(animator);
        }

        /// <summary>Reads the mask again (after code changed it).</summary>
        public void RefreshMask() => _weights = (Pose != null && Pose.Mask != null ? Pose.Mask : new BoneMask()).Weights(_animator);

        /// <summary>How strongly the pose takes this bone, 0..1.</summary>
        public float WeightOf(HumanBodyBones bone) => _weights[(int)bone];

        private void SetRotations(PoseAsset pose)
        {
            for (int i = 0; i < _bones.Length; i++)
                _rotations[i] = _bones[i] != null ? _bones[i].localRotation : Quaternion.identity;
            bool[] saved = new bool[_bones.Length];
            foreach (PoseAsset.BoneRotation bone in pose.Rotations)
            {
                int i = (int)bone.Bone;
                if (i >= 0 && i < _rotations.Length)
                {
                    _rotations[i] = bone.Rotation;
                    saved[i] = true;
                }
            }
            // A bone the save does not have would snap to whatever it held now: leave it to the Animator.
            _saved = saved;
        }

        private bool[] _saved;

        /// <summary>
        /// Turns every masked bone from where the Animator put it toward the pose, by the mask's weight
        /// times weight (0: animation only, 1: the pose on fully masked bones; above 1 overshoots).
        /// </summary>
        public void Apply(float weight)
        {
            if (weight == 0f)
                return;
            for (int i = 0; i < _bones.Length; i++)
            {
                if (_bones[i] == null || _weights[i] <= 0f || _saved != null && !_saved[i])
                    continue;
                _bones[i].localRotation = Quaternion.SlerpUnclamped(_bones[i].localRotation, _rotations[i], _weights[i] * weight);
            }
        }
    }
}
