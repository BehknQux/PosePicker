using System;
using System.Collections.Generic;
using UnityEngine;

namespace PosePicker
{
    /// <summary>
    /// Which bones a layered pose takes, like Unreal's Layered blend per bone: a list of branches, each
    /// a bone and everything under it. BlendDepth lets the pose fade in along the branch instead of
    /// starting at full strength on the branch's first bone: with depth N the branch bone gets 1/(N+1),
    /// the next bone down 2/(N+1), and so on until full strength N bones down (0: full at once).
    /// An Exclude branch takes its bones back out of the pose (with its own fade the same way), so
    /// "Spine, minus the left arm" is two branches. Excludes count after every include.
    /// No branch: the whole body.
    /// Plain data, so code can change it too; call ModelPose.RefreshMask after changing a playing pose's.
    /// </summary>
    [Serializable]
    public class BoneMask
    {
        [Serializable]
        public struct Branch
        {
            public HumanBodyBones Bone;
            [Tooltip("Bones down the branch over which the pose fades in. 0: full strength from this bone.")]
            [Min(0)] public int BlendDepth;
            [Tooltip("Takes this branch out of the pose instead of putting it in.")]
            public bool Exclude;
        }

        public List<Branch> Branches = new();

        public bool IsWholeBody => Branches == null || Branches.Count == 0;

        public BoneMask Copy() => new() { Branches = Branches != null ? new List<Branch>(Branches) : new List<Branch>() };

        /// <summary>
        /// How strongly the pose takes each bone of this body, 0..1, indexed by HumanBodyBones. Read from the
        /// body's own hierarchy, so depth counts the bones it really has (a rig without Upper chest counts
        /// one fewer). A bone the body lacks gets 0.
        /// </summary>
        public float[] Weights(Animator animator)
        {
            var weights = new float[(int)HumanBodyBones.LastBone];
            if (animator == null || !animator.isHuman)
                return weights;

            var bones = new Transform[weights.Length];
            var ids = new Dictionary<Transform, int>();
            for (int i = 0; i < bones.Length; i++)
            {
                bones[i] = animator.GetBoneTransform((HumanBodyBones)i);
                if (bones[i] != null)
                    ids[bones[i]] = i;
            }

            if (IsWholeBody)
            {
                for (int i = 0; i < weights.Length; i++)
                    weights[i] = bones[i] != null ? 1f : 0f;
                return weights;
            }

            foreach (Branch branch in Branches)
                if (!branch.Exclude)
                    for (int i = 0; i < bones.Length; i++)
                        if (Strength(bones[i], branch, bones, ids, out float w))
                            weights[i] = Mathf.Max(weights[i], w);
            foreach (Branch branch in Branches)
                if (branch.Exclude)
                    for (int i = 0; i < bones.Length; i++)
                        if (Strength(bones[i], branch, bones, ids, out float w))
                            weights[i] *= 1f - w;
            return weights;
        }

        /// <summary>
        /// Whether the bone lies in the branch, and how strongly by its depth: the humanoid bones from the
        /// branch's bone down to it, the branch's bone being depth 0.
        /// </summary>
        private static bool Strength(Transform bone, Branch branch, Transform[] bones, Dictionary<Transform, int> ids,
            out float weight)
        {
            weight = 0f;
            int rootIndex = (int)branch.Bone;
            if (bone == null || rootIndex < 0 || rootIndex >= bones.Length || bones[rootIndex] == null)
                return false;
            Transform root = bones[rootIndex];
            int depth = 0;
            for (Transform t = bone; t != null; t = t.parent)
            {
                if (t == root)
                {
                    weight = Mathf.Min(1f, (depth + 1f) / (branch.BlendDepth + 1f));
                    return true;
                }
                if (ids.ContainsKey(t))
                    depth++;
            }
            return false;
        }
    }
}
