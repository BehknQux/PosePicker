using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

namespace PosePicker.Editor
{
    /// <summary>
    /// Picks a single pose out of an animation clip, shapes it, and makes an Animator state hold it.
    /// The window is in foldout sections:
    /// - Source: model (a scene object, or a prefab / FBX that gets a temporary preview copy),
    ///   controller, state, clip and frame.
    /// - Pose: body rotation around the hips, and bones in the Scene view. Rotate mode: dots on the main
    ///   bones, click one for a rotation gizmo. IK mode: drag a hand or foot and the arm or leg follows
    ///   (two-bone IK), shoulder or hip staying put; a small ball sets the elbow or knee side. IK only
    ///   helps editing: the result is stored as bone turns.
    /// - Mask: which bones take the pose (Unreal's Layered blend per bone): branches, each a bone and
    ///   everything under it, with a fade-in depth and an exclude switch. No branch: the whole body,
    ///   held by an Animator state. With branches the pose is layered: code plays it with
    ///   ModelPose.PlayLayered, the rest of the body keeps whatever the Animator plays. The preview
    ///   plays an animation underneath (e.g. a run) with the pose held on top, or blending in and out.
    /// - Aim: whether the pose leans to an action's aim (ModelPose.Lean), with a sample aim angle shown as
    ///   a draggable arrow.
    /// - Blend: how the body moves into the pose (time and a hand-drawn curve, with presets). The preview
    ///   plays a previous animation from a chosen frame, then blends into the pose from a second chosen
    ///   frame, as the game does. The previous animation is picked from a list that scans the controller
    ///   (states that lead into this one, every state, or every clip in the project).
    /// - Library: save the pose by name as a PoseAsset, load one back. The save folder is a project
    ///   setting (PosePickerSettings).
    /// "Apply" sets the state's motion to the clip, its speed to 0, its Cycle Offset to the picked
    /// moment, stores the rest as the state's PoseOffset (played by ModelPose in game), sets the
    /// transitions into the state to 0 s (the pose's own blend replaces them) and turns the clip's
    /// Loop Time on: a state's Cycle Offset is ignored on a clip that does not loop, and the state then
    /// held the clip's first frame instead of the picked one.
    /// The preview plays the clips through the model's Animator with root motion off (as game
    /// Animators often are), so the body stands where it does in game, not where the clip's travel takes it.
    /// </summary>
    public class PosePickerWindow : EditorWindow
    {
        private enum Mode { Rotate, IK }

        // Bones offered for adjusting. Hips are left out: the body rotation turns the whole model.
        private static readonly HumanBodyBones[] EditableBones =
        {
            HumanBodyBones.Spine, HumanBodyBones.Chest, HumanBodyBones.UpperChest, HumanBodyBones.Neck, HumanBodyBones.Head,
            HumanBodyBones.LeftShoulder, HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand,
            HumanBodyBones.RightShoulder, HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand,
            HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftFoot, HumanBodyBones.LeftToes,
            HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot, HumanBodyBones.RightToes,
        };

        // Two-bone IK chains: (upper, lower, end).
        private static readonly HumanBodyBones[][] Limbs =
        {
            new[] { HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand },
            new[] { HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand },
            new[] { HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftFoot },
            new[] { HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot },
        };

        private static readonly Dictionary<HumanBodyBones, string> BoneNames = new()
        {
            { HumanBodyBones.Spine, "Spine" }, { HumanBodyBones.Chest, "Chest" }, { HumanBodyBones.UpperChest, "Upper chest" },
            { HumanBodyBones.Neck, "Neck" }, { HumanBodyBones.Head, "Head" },
            { HumanBodyBones.LeftShoulder, "Left shoulder" }, { HumanBodyBones.LeftUpperArm, "Left upper arm" },
            { HumanBodyBones.LeftLowerArm, "Left lower arm" }, { HumanBodyBones.LeftHand, "Left hand" },
            { HumanBodyBones.RightShoulder, "Right shoulder" }, { HumanBodyBones.RightUpperArm, "Right upper arm" },
            { HumanBodyBones.RightLowerArm, "Right lower arm" }, { HumanBodyBones.RightHand, "Right hand" },
            { HumanBodyBones.LeftUpperLeg, "Left upper leg" }, { HumanBodyBones.LeftLowerLeg, "Left lower leg" },
            { HumanBodyBones.LeftFoot, "Left foot" }, { HumanBodyBones.LeftToes, "Left toes" },
            { HumanBodyBones.RightUpperLeg, "Right upper leg" }, { HumanBodyBones.RightLowerLeg, "Right lower leg" },
            { HumanBodyBones.RightFoot, "Right foot" }, { HumanBodyBones.RightToes, "Right toes" },
        };

        // Source.
        private GameObject _model;
        private AnimatorController _controller;
        private AnimationClip _clip;
        private int _stateIndex;
        private float _time;

        // The pose being made. Serialized so Undo can record the window.
        [SerializeField] private Vector3 _rotation;
        [SerializeField] private List<PoseOffset.BoneOffset> _bones = new();
        [SerializeField] private bool _followAim;
        [SerializeField] private float _blendIn = 0.1f;
        [SerializeField] private AnimationCurve _blendShape = AnimationCurve.Linear(0f, 0f, 1f, 1f);
        [SerializeField] private Mode _mode;
        [SerializeField] private BoneMask _mask = new();
        [SerializeField] private float _blendOut = 0.2f;
        private HumanBodyBones _selected = HumanBodyBones.LastBone;

        // Preview-only settings: a sample aim, and what plays before the blend.
        [SerializeField] private float _sampleAim;
        private enum SourceKind { IntoState, AllStates, AllClips }
        private SourceKind _sourceKind;
        private int _sourceIndex;
        private readonly List<string> _sourceNames = new();
        private readonly List<AnimationClip> _sourceClips = new();
        private AnimatorState _sourcesFor;
        private AnimationClip _fromClip;
        private float _leadStart;   // where the previous animation starts playing in the preview
        private float _fromTime;    // where it is when the blend begins
        private bool _showBlend;
        private float _timeline;    // preview seconds: the previous animation, the blend, then a short hold
        private bool _playing;
        private double _lastTick;
        private const float Hold = 0.4f;

        // Layered preview: an animation playing underneath, the pose held on it or blending in and out.
        private enum LayerPreview { Off, Hold, Blend }
        private LayerPreview _layerPreview;
        private AnimationClip _baseClip;
        private PlayableGraph _baseGraph;
        private AnimationClip _baseGraphClip;
        private float _baseTime;
        private double _baseTick;
        private const float LayerRest = 0.6f, LayerHold = 1f;

        // Library.
        private string _poseName = "NewPose";
        private PoseAsset _loadPose;

        // Foldouts.
        [SerializeField] private bool _foldSource = true, _foldPose = true, _foldMask = true, _foldAim = true, _foldBlend = true, _foldLibrary = true;

        private readonly List<AnimatorState> _states = new();
        private readonly List<string> _stateNames = new();

        // Preview.
        private GameObject _previewCopy;
        private Animator _previewAnimator;
        private PlayableGraph _graph, _fromGraph;
        private AnimationClip _graphClip, _fromGraphClip;
        private Transform _previewModel, _previewHips;
        private Vector3 _previewBasePosition;
        private Quaternion _previewBaseRotation;
        private PoseSnapshot _previewSnapshot;
        // Each bone's rotation as the clip gives it, before our turns: offsets are measured from it.
        private readonly Dictionary<HumanBodyBones, Quaternion> _animatedLocal = new();
        // Where each limb's elbow or knee points (world), set when the limb is first picked.
        private readonly Dictionary<HumanBodyBones, Vector3> _poles = new();

        [MenuItem("Tools/Pose Picker")]
        private static void Open() => GetWindow<PosePickerWindow>("Pose Picker");

        private void OnEnable()
        {
            SceneView.duringSceneGui += OnSceneGUI;
            Undo.undoRedoPerformed += OnUndoRedo;
            EditorApplication.update += Tick;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
        }

        private void OnPlayModeChanged(PlayModeStateChange change)
        {
            if (change == PlayModeStateChange.ExitingEditMode)
                StopPreview();
        }

        private void OnDisable()
        {
            SceneView.duringSceneGui -= OnSceneGUI;
            Undo.undoRedoPerformed -= OnUndoRedo;
            EditorApplication.update -= Tick;
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            StopPreview();
        }

        private void OnUndoRedo()
        {
            if (IsPreviewing)
                Sample();
            Repaint();
        }

        // Plays the blend preview once, over the blend time; the layered preview keeps looping.
        private void Tick()
        {
            if (_layerPreview != LayerPreview.Off)
            {
                double clock = EditorApplication.timeSinceStartup;
                _baseTime += (float)(clock - _baseTick);
                _baseTick = clock;
                if (IsPreviewing)
                    Sample();
                Repaint();
                return;
            }
            if (!_playing)
                return;
            double now = EditorApplication.timeSinceStartup;
            float delta = (float)(now - _lastTick);
            _lastTick = now;
            _timeline = Mathf.Min(TimelineLength, _timeline + delta);
            if (_timeline >= TimelineLength)
                _playing = false;
            if (IsPreviewing)
                Sample();
            Repaint();
        }

        private void OnGUI()
        {
            KeepPreviewAlive();
            // Changing the pose itself shows the pose again, not the blend preview.
            EditorGUI.BeginChangeCheck();
            AnimatorState state = DrawSourceSection();
            if (_clip == null)
            {
                EditorGUI.EndChangeCheck();
                EditorGUILayout.HelpBox("Pick a clip.", MessageType.Info);
                DrawBottomButtons(state, 0f);
                return;
            }
            float normalized = _clip.length > 0f ? _time / _clip.length : 0f;

            DrawPoseSection();
            DrawMaskSection();
            DrawAimSection();
            if (EditorGUI.EndChangeCheck())
                ShowPose();
            DrawBlendSection(state);
            DrawLibrarySection();

            if (state != null)
            {
                string current = state.motion != null ? state.motion.name : "none";
                PoseOffset saved = OffsetOf(state);
                EditorGUILayout.Space();
                EditorGUILayout.LabelField($"{state.name} now",
                    $"{current}, normalized {state.cycleOffset:0.000}, " +
                    (saved != null
                        ? $"rotation {saved.Rotation}, {saved.Bones.Count} bones, aim {(saved.FollowAim ? "on" : "off")}, blend {saved.BlendIn:0.##} s"
                        : "no pose settings"),
                    EditorStyles.wordWrappedMiniLabel);
            }

            DrawBottomButtons(state, normalized);

            if (IsPreviewing)
                Sample();
        }

        // --- Sections ---------------------------------------------------------------------------

        private AnimatorState DrawSourceSection()
        {
            AnimatorState state = _states.Count > 0 ? _states[Mathf.Clamp(_stateIndex, 0, _states.Count - 1)] : null;
            _foldSource = EditorGUILayout.BeginFoldoutHeaderGroup(_foldSource, "1. Source");
            if (_foldSource)
            {
                EditorGUI.BeginChangeCheck();
                _model = (GameObject)EditorGUILayout.ObjectField(new GUIContent("Model",
                    "A scene object, or a prefab / FBX (previewed on a temporary copy)."), _model, typeof(GameObject), true);
                if (EditorGUI.EndChangeCheck())
                {
                    StopPreview();
                    Animator found = _model != null ? _model.GetComponentInChildren<Animator>() : null;
                    if (found != null && found.runtimeAnimatorController is AnimatorController c)
                        SetController(c);
                }

                EditorGUI.BeginChangeCheck();
                AnimatorController controller = (AnimatorController)EditorGUILayout.ObjectField(
                    "Controller", _controller, typeof(AnimatorController), false);
                if (EditorGUI.EndChangeCheck())
                    SetController(controller);

                if (_states.Count > 0)
                {
                    EditorGUI.BeginChangeCheck();
                    _stateIndex = EditorGUILayout.Popup("State",Mathf.Clamp(_stateIndex, 0, _states.Count - 1), _stateNames.ToArray());
                    state = _states[_stateIndex];
                    if (EditorGUI.EndChangeCheck())
                    {
                        _clip = null;
                        LoadFrom(state);
                    }
                }
            }

            if (_clip == null && state != null && state.motion is AnimationClip stateClip)
            {
                _clip = stateClip;
                _time = state.cycleOffset * stateClip.length;
            }

            if (_foldSource)
            {
                EditorGUI.BeginChangeCheck();
                _clip = (AnimationClip)EditorGUILayout.ObjectField(new GUIContent("Clip", "If empty, the state's clip."), _clip, typeof(AnimationClip), false);
                if (EditorGUI.EndChangeCheck())
                    _time = 0f;
                if (_clip != null)
                    _time = FrameSlider("Frame",_clip, _time);
            }
            EditorGUILayout.EndFoldoutHeaderGroup();
            return state;
        }

        private void DrawPoseSection()
        {
            _foldPose = EditorGUILayout.BeginFoldoutHeaderGroup(_foldPose, "2. Pose");
            if (_foldPose)
            {
                GUI.enabled = !Layered;
                EditorGUI.BeginChangeCheck();
                Vector3 rotation = EditorGUILayout.Vector3Field(new GUIContent("Body rotation (degrees)",
                    "The whole body turns around the hips. X: pitch (head down), Y: turn left / right, Z: roll to the side."), _rotation);
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(this, "Pose Picker: body rotation");
                    _rotation = rotation;
                }
                GUI.enabled = true;
                if (Layered)
                    EditorGUILayout.LabelField(" ", "Body rotation is for whole-body poses; a layered pose turns only its bones.", EditorStyles.wordWrappedMiniLabel);

                EditorGUI.BeginChangeCheck();
                _mode = (Mode)GUILayout.Toolbar((int)_mode, new[] { "Rotate", "IK (hand / foot)" });
                if (EditorGUI.EndChangeCheck())
                {
                    _selected = HumanBodyBones.LastBone;
                    SceneView.RepaintAll();
                }
                EditorGUILayout.LabelField(_mode == Mode.Rotate
                        ? "Click a bone dot in the Scene view, then turn it with the rotation gizmo."
                        : "Drag a hand or foot and the arm / leg follows. The small purple ball sets the elbow / knee direction.",
                    EditorStyles.wordWrappedMiniLabel);

                if (_selected != HumanBodyBones.LastBone)
                {
                    Quaternion offset = OffsetFor(_selected);
                    EditorGUI.BeginChangeCheck();
                    Vector3 euler = EditorGUILayout.Vector3Field(BoneNames[_selected] + " (degrees)", Signed(offset.eulerAngles));
                    if (EditorGUI.EndChangeCheck())
                    {
                        Undo.RecordObject(this, "Pose Picker: bone");
                        SetOffset(_selected, Quaternion.Euler(euler));
                    }
                    if (GUILayout.Button("Reset " + BoneNames[_selected]))
                    {
                        Undo.RecordObject(this, "Pose Picker: reset bone");
                        SetOffset(_selected, Quaternion.identity);
                        if (IsLimbEnd(_selected, out HumanBodyBones[] limb))
                        {
                            SetOffset(limb[0], Quaternion.identity);
                            SetOffset(limb[1], Quaternion.identity);
                            _poles.Remove(_selected);
                        }
                    }
                }
                else if (!IsPreviewing)
                    EditorGUILayout.LabelField(" ", "Preview to adjust bones.");

                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField($"{_bones.Count} bones changed");
                    GUI.enabled = _bones.Count > 0;
                    if (GUILayout.Button("Reset all bones"))
                    {
                        Undo.RecordObject(this, "Pose Picker: reset all bones");
                        _bones.Clear();
                        _poles.Clear();
                    }
                    GUI.enabled = true;
                }
            }
            EditorGUILayout.EndFoldoutHeaderGroup();
        }

        private void DrawAimSection()
        {
            _foldAim = EditorGUILayout.BeginFoldoutHeaderGroup(_foldAim, "3. Aim");
            if (_foldAim && Layered)
                EditorGUILayout.LabelField(" ", "Aim is for whole-body poses.", EditorStyles.miniLabel);
            else if (_foldAim)
            {
                EditorGUI.BeginChangeCheck();
                bool follow = EditorGUILayout.Toggle(new GUIContent("Follow aim",
                    "If on, in game the body also leans on top of the pose toward the action's aim (e.g. where the camera looks)."), _followAim);
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(this, "Pose Picker: aim");
                    _followAim = follow;
                }
                GUI.enabled = _followAim;
                EditorGUI.BeginChangeCheck();
                float aim = EditorGUILayout.Slider(new GUIContent("Sample aim (degrees)",
                    "Preview only: + down, − up. You can also drag the arrow in the Scene view."), _sampleAim, -89f, 89f);
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(this, "Pose Picker: sample aim");
                    _sampleAim = aim;
                }
                GUI.enabled = true;
            }
            EditorGUILayout.EndFoldoutHeaderGroup();
        }

        private void DrawBlendSection(AnimatorState state)
        {
            _foldBlend = EditorGUILayout.BeginFoldoutHeaderGroup(_foldBlend, "4. Blend");
            if (_foldBlend)
            {
                EditorGUI.BeginChangeCheck();
                float blendIn = EditorGUILayout.FloatField(new GUIContent("Duration (s)", "How long the body takes to move into this pose. 0: instant."), _blendIn);
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(this, "Pose Picker: blend duration");
                    _blendIn = Mathf.Max(0f, blendIn);
                }

                EditorGUI.BeginChangeCheck();
                AnimationCurve shape = EditorGUILayout.CurveField(new GUIContent("Curve",
                        "Click to draw: horizontal is time (0..1), vertical is how far into the pose (0 previous stance, 1 pose; above 1 overshoots the pose and comes back)."),
                    _blendShape, new Color(0.4f, 0.8f, 1f), new Rect(0f, -0.5f, 1f, 2f), GUILayout.Height(48));
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(this, "Pose Picker: curve");
                    _blendShape = shape;
                }
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Space(EditorGUIUtility.labelWidth);
                    if (GUILayout.Button("Linear", EditorStyles.miniButtonLeft))
                        SetShape(AnimationCurve.Linear(0f, 0f, 1f, 1f));
                    if (GUILayout.Button("Smooth", EditorStyles.miniButtonMid))
                        SetShape(AnimationCurve.EaseInOut(0f, 0f, 1f, 1f));
                    if (GUILayout.Button("Fast start", EditorStyles.miniButtonMid))
                        SetShape(new AnimationCurve(new Keyframe(0f, 0f, 2f, 2f), new Keyframe(1f, 1f, 0f, 0f)));
                    if (GUILayout.Button("Slow start", EditorStyles.miniButtonMid))
                        SetShape(new AnimationCurve(new Keyframe(0f, 0f, 0f, 0f), new Keyframe(1f, 1f, 2f, 2f)));
                    if (GUILayout.Button("Overshoot", EditorStyles.miniButtonRight))
                        SetShape(new AnimationCurve(new Keyframe(0f, 0f, 0f, 3f), new Keyframe(0.6f, 1.15f, 0f, 0f), new Keyframe(1f, 1f, 0f, 0f)));
                }

                if (Layered)
                {
                    EditorGUI.BeginChangeCheck();
                    float blendOut = EditorGUILayout.FloatField(new GUIContent("Out (s)",
                        "How long the bones take to go back to the animation underneath when the pose stops."), _blendOut);
                    if (EditorGUI.EndChangeCheck())
                    {
                        Undo.RecordObject(this, "Pose Picker: blend out");
                        _blendOut = Mathf.Max(0f, blendOut);
                    }
                    EditorGUILayout.LabelField(" ", "See it with the animation underneath: Mask section, Play blend.", EditorStyles.miniLabel);
                    EditorGUILayout.EndFoldoutHeaderGroup();
                    return;
                }

                EditorGUILayout.Space(4);
                EditorGUILayout.LabelField("Preview: previous animation", EditorStyles.miniBoldLabel);
                DrawSourcePicker(state);
                if (_fromClip != null)
                {
                    _leadStart = FrameSlider("Start playing at", _fromClip, _leadStart);
                    _fromTime = FrameSlider("Blend starts at frame", _fromClip, _fromTime);
                }

                GUI.enabled = _fromClip != null && IsPreviewing;
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button(_playing ? "Stop" : "Play", GUILayout.Width(80)))
                    {
                        _showBlend = true;
                        _playing = !_playing;
                        if (_playing)
                        {
                            _timeline = 0f;
                            _lastTick = EditorApplication.timeSinceStartup;
                        }
                    }
                    EditorGUI.BeginChangeCheck();
                    float t = EditorGUILayout.Slider(_timeline, 0f, Mathf.Max(0.01f, TimelineLength));
                    if (EditorGUI.EndChangeCheck())
                    {
                        _showBlend = true;
                        _playing = false;
                        _timeline = t;
                    }
                }
                if (_fromClip != null)
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUILayout.LabelField(" ", !BlendVisible ? "Showing pose"
                            : _timeline < LeadLength ? "Previous animation playing" : "Blending", EditorStyles.miniLabel);
                        if (BlendVisible && GUILayout.Button("Show pose", EditorStyles.miniButton, GUILayout.Width(90)))
                            ShowPose();
                    }
                GUI.enabled = true;
            }
            EditorGUILayout.EndFoldoutHeaderGroup();
        }

        private void SetShape(AnimationCurve shape)
        {
            Undo.RecordObject(this, "Pose Picker: curve");
            _blendShape = shape;
        }

        /// <summary>
        /// What plays before the pose: a kind (states that lead into this one in the Animator, every state
        /// of the controller, every clip of the project), then one entry of it. A blend tree state lists
        /// each of its clips. A clip can also be dragged in by hand.
        /// </summary>
        private void DrawSourcePicker(AnimatorState state)
        {
            EditorGUI.BeginChangeCheck();
            _sourceKind = (SourceKind)EditorGUILayout.Popup("Pick from", (int)_sourceKind,
                new[] { "States leading into this one", "All states in the controller", "All clips in the project" });
            if (EditorGUI.EndChangeCheck() || _sourcesFor != state || (_sourceNames.Count == 0 && _sourceKind == SourceKind.AllClips))
                RefreshSources(state);

            if (_sourceNames.Count > 0)
            {
                EditorGUI.BeginChangeCheck();
                _sourceIndex = EditorGUILayout.Popup("Previous animation",Mathf.Clamp(_sourceIndex, 0, _sourceNames.Count - 1), _sourceNames.ToArray());
                if (EditorGUI.EndChangeCheck() || _fromClip == null)
                    SetFromClip(_sourceClips[_sourceIndex]);
            }
            else if (_sourceKind == SourceKind.IntoState)
                EditorGUILayout.HelpBox("No transition leads into this state in the Animator (it may be entered from code). " +
                                        "Try 'All states in the controller'.", MessageType.None);

            EditorGUI.BeginChangeCheck();
            AnimationClip clip = (AnimationClip)EditorGUILayout.ObjectField("Clip", _fromClip, typeof(AnimationClip), false);
            if (EditorGUI.EndChangeCheck())
                SetFromClip(clip);
        }

        private void SetFromClip(AnimationClip clip)
        {
            if (clip == _fromClip)
                return;
            _fromClip = clip;
            _leadStart = 0f;
            _fromTime = clip != null ? clip.length : 0f;
            _timeline = 0f;
        }

        private void RefreshSources(AnimatorState state)
        {
            _sourcesFor = state;
            _sourceNames.Clear();
            _sourceClips.Clear();
            _sourceIndex = 0;
            switch (_sourceKind)
            {
                case SourceKind.IntoState:
                    if (_controller == null || state == null)
                        break;
                    foreach (AnimatorControllerLayer layer in _controller.layers)
                        CollectInto(layer.stateMachine, state);
                    break;
                case SourceKind.AllStates:
                    for (int i = 0; i < _states.Count; i++)
                        if (_states[i] != state)
                            AddMotion(_stateNames[i], _states[i].motion);
                    break;
                case SourceKind.AllClips:
                    foreach (string guid in AssetDatabase.FindAssets("t:AnimationClip", new[] { "Assets" }))
                    {
                        string path = AssetDatabase.GUIDToAssetPath(guid);
                        foreach (Object asset in AssetDatabase.LoadAllAssetsAtPath(path))
                            if (asset is AnimationClip clip && !clip.name.StartsWith("__preview__"))
                                AddSource($"{System.IO.Path.GetFileNameWithoutExtension(path)} / {clip.name}", clip);
                    }
                    break;
            }
        }

        // States with a transition into the target; an Any State transition means every state can.
        private void CollectInto(AnimatorStateMachine machine, AnimatorState target)
        {
            bool fromAny = false;
            foreach (AnimatorStateTransition transition in machine.anyStateTransitions)
                fromAny |= transition.destinationState == target;
            for (int i = 0; i < _states.Count; i++)
            {
                AnimatorState candidate = _states[i];
                if (candidate == target)
                    continue;
                bool leads = fromAny;
                foreach (AnimatorStateTransition transition in candidate.transitions)
                    leads |= transition.destinationState == target;
                if (leads)
                    AddMotion(_stateNames[i] + (fromAny ? " (from Any State)" : ""), candidate.motion);
            }
        }

        private void AddMotion(string name, Motion motion)
        {
            if (motion is AnimationClip clip)
                AddSource(name, clip);
            else if (motion is BlendTree tree)
                foreach (ChildMotion child in tree.children)
                    AddMotion($"{name} / {(child.motion != null ? child.motion.name : "?")}", child.motion);
        }

        private void AddSource(string name, AnimationClip clip)
        {
            if (clip == null || _sourceClips.Contains(clip) && _sourceKind != SourceKind.AllStates)
                return;
            _sourceNames.Add(name);
            _sourceClips.Add(clip);
        }

        private void DrawLibrarySection()
        {
            _foldLibrary = EditorGUILayout.BeginFoldoutHeaderGroup(_foldLibrary, "5. Library");
            if (_foldLibrary)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    _poseName = EditorGUILayout.TextField("Pose name", _poseName);
                    GUI.enabled = !string.IsNullOrWhiteSpace(_poseName);
                    if (GUILayout.Button("Save pose", GUILayout.Width(110)))
                        SavePose();
                    GUI.enabled = true;
                }
                using (new EditorGUILayout.HorizontalScope())
                {
                    _loadPose = (PoseAsset)EditorGUILayout.ObjectField("Saved pose", _loadPose, typeof(PoseAsset), false);
                    GUI.enabled = _loadPose != null;
                    if (GUILayout.Button("Load", GUILayout.Width(110)))
                        LoadPose(_loadPose);
                    GUI.enabled = true;
                }
                DrawPoseFolder();
            }
            EditorGUILayout.EndFoldoutHeaderGroup();
        }

        private void DrawBottomButtons(AnimatorState state, float normalized)
        {
            EditorGUILayout.Space();
            using (new EditorGUILayout.HorizontalScope())
            {
                GUI.enabled = _model != null && _clip != null;
                if (GUILayout.Button(IsPreviewing ? "Stop preview" : "Preview", GUILayout.Height(24)))
                {
                    if (IsPreviewing)
                        StopPreview();
                    else
                        StartPreview();
                }
                GUI.enabled = state != null && _clip != null && !Layered;
                if (GUILayout.Button(state != null ? $"Apply pose to {state.name}" : "Apply pose to state", GUILayout.Height(24)))
                    Apply(state, normalized);
                GUI.enabled = true;
            }
            if (Layered)
                EditorGUILayout.HelpBox("A layered pose is not put on a state: save it in the Library, and code plays it with " +
                                        "ModelPose.PlayLayered(pose).", MessageType.None);
            if (_model == null)
                EditorGUILayout.HelpBox("Pick a model to preview.", MessageType.Info);
        }

        private static float FrameSlider(string label, AnimationClip clip, float time)
        {
            float frameRate = clip.frameRate > 0f ? clip.frameRate : 30f;
            int lastFrame = Mathf.Max(1, Mathf.RoundToInt(clip.length * frameRate));
            int frame = EditorGUILayout.IntSlider(label, Mathf.RoundToInt(time * frameRate), 0, lastFrame);
            time = Mathf.Min(frame / frameRate, clip.length);
            float normalized = clip.length > 0f ? time / clip.length : 0f;
            EditorGUILayout.LabelField(" ", $"{time:0.000} s  /  {clip.length:0.000} s   (normalized {normalized:0.000})", EditorStyles.miniLabel);
            return time;
        }

        // --- Library ----------------------------------------------------------------------------

        private static void DrawPoseFolder()
        {
            PosePickerSettings settings = PosePickerSettings.instance;
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("Save folder", settings.PoseFolder);
                if (GUILayout.Button("Change...", GUILayout.Width(110)))
                {
                    string picked = EditorUtility.OpenFolderPanel("Pose save folder", settings.PoseFolder, "");
                    if (!string.IsNullOrEmpty(picked))
                    {
                        string assets = Application.dataPath;
                        picked = picked.Replace('\\', '/');
                        if (picked == assets || picked.StartsWith(assets + "/"))
                            settings.PoseFolder = "Assets" + picked.Substring(assets.Length);
                        else
                            EditorUtility.DisplayDialog("Pose Picker", "Pick a folder inside this project's Assets folder.", "OK");
                    }
                }
            }
        }

        private void SavePose()
        {
            string fileName = string.Join("_", _poseName.Trim().Split(System.IO.Path.GetInvalidFileNameChars()));
            PosePickerSettings settings = PosePickerSettings.instance;
            settings.EnsurePoseFolder();
            string path = $"{settings.PoseFolder}/{fileName}.asset";
            PoseAsset pose = AssetDatabase.LoadAssetAtPath<PoseAsset>(path);
            if (pose != null && !EditorUtility.DisplayDialog("Pose Picker", $"'{fileName}' already exists. Overwrite it?", "Overwrite", "Cancel"))
                return;
            if (pose == null)
            {
                pose = CreateInstance<PoseAsset>();
                AssetDatabase.CreateAsset(pose, path);
            }
            Undo.RecordObject(pose, "Pose Picker: save pose");
            pose.Clip = _clip;
            pose.Time = _time;
            pose.Rotation = _rotation;
            pose.Bones = new List<PoseOffset.BoneOffset>(_bones);
            pose.FollowAim = _followAim;
            pose.BlendIn = _blendIn;
            pose.BlendShape = new AnimationCurve(_blendShape.keys);
            pose.Mask = _mask.Copy();
            pose.BlendOut = _blendOut;
            List<PoseAsset.BoneRotation> rotations = BakeRotations();
            if (rotations != null)
                pose.Rotations = rotations;
            else
                Debug.LogWarning("Pose Picker: no humanoid model to read the pose's bones from; pick a model and save again " +
                                 "before playing it as a layered pose.");
            EditorUtility.SetDirty(pose);
            AssetDatabase.SaveAssets();
            _loadPose = pose;
            EditorGUIUtility.PingObject(pose);
            Debug.Log($"Pose Picker: saved '{fileName}' ({path}).");
        }

        private void LoadPose(PoseAsset pose)
        {
            Undo.RecordObject(this, "Pose Picker: load pose");
            _clip = pose.Clip;
            _time = pose.Time;
            _rotation = pose.Rotation;
            _bones = new List<PoseOffset.BoneOffset>(pose.Bones);
            _followAim = pose.FollowAim;
            _blendIn = pose.BlendIn;
            _blendShape = pose.BlendShape != null ? new AnimationCurve(pose.BlendShape.keys) : AnimationCurve.Linear(0f, 0f, 1f, 1f);
            _mask = pose.Mask != null ? pose.Mask.Copy() : new BoneMask();
            _blendOut = pose.BlendOut;
            _poseName = pose.name;
            _poles.Clear();
            _selected = HumanBodyBones.LastBone;
        }

        // --- Scene view -------------------------------------------------------------------------

        private void OnSceneGUI(SceneView view)
        {
            if (!IsPreviewing || !_previewAnimator.isHuman)
                return;

            if (_followAim)
                DrawAimArrow();

            // Bones are edited on the finished pose; mid-blend or over an animation the body is somewhere in between.
            if (_layerPreview != LayerPreview.Off)
            {
                Handles.BeginGUI();
                GUI.Label(new Rect(10, 10, 360, 20), "Animation playing underneath: stop it to edit bones.");
                Handles.EndGUI();
                return;
            }
            if (BlendVisible)
            {
                Handles.BeginGUI();
                GUI.Label(new Rect(10, 10, 360, 20), "Showing the blend: move the slider to the end to edit bones.");
                Handles.EndGUI();
                return;
            }

            float[] maskWeights = Layered ? _mask.Weights(_previewAnimator) : null;
            foreach (HumanBodyBones id in _mode == Mode.IK ? LimbEnds() : EditableBones)
            {
                Transform bone = _previewAnimator.GetBoneTransform(id);
                if (bone == null)
                    continue;
                float size = HandleUtility.GetHandleSize(bone.position) * 0.05f;
                Handles.color = id == _selected ? Color.yellow
                    : _mode == Mode.IK ? new Color(1f, 0.5f, 0.3f) : new Color(0.3f, 0.9f, 0.7f);
                // Layered: bones outside the mask grey, partly masked ones in between.
                if (maskWeights != null && id != _selected)
                    Handles.color = Color.Lerp(new Color(0.45f, 0.45f, 0.45f), Handles.color, maskWeights[(int)id]);
                if (Handles.Button(bone.position, Quaternion.identity, size, size * 1.3f, Handles.SphereHandleCap))
                {
                    _selected = id;
                    Repaint();
                }
            }

            if (_selected == HumanBodyBones.LastBone)
                return;
            Transform selected = _previewAnimator.GetBoneTransform(_selected);
            if (selected == null)
                return;

            if (_mode == Mode.Rotate)
            {
                EditorGUI.BeginChangeCheck();
                Quaternion turned = Handles.RotationHandle(selected.rotation, selected.position);
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(this, "Pose Picker: rotate bone");
                    selected.rotation = turned;
                    StoreFromPose(_selected);
                    Sample();
                    Repaint();
                }
            }
            else if (IsLimbEnd(_selected, out HumanBodyBones[] limb))
            {
                Transform upper = _previewAnimator.GetBoneTransform(limb[0]);
                Transform lower = _previewAnimator.GetBoneTransform(limb[1]);
                if (upper == null || lower == null)
                    return;
                if (!_poles.ContainsKey(_selected))
                    _poles[_selected] = DefaultPole(upper, lower, selected);

                EditorGUI.BeginChangeCheck();
                Vector3 target = Handles.PositionHandle(selected.position, Quaternion.identity);
                Handles.color = new Color(0.6f, 0.5f, 1f);
                Handles.DrawDottedLine(lower.position, _poles[_selected], 3f);
                float poleSize = HandleUtility.GetHandleSize(_poles[_selected]) * 0.06f;
                Vector3 pole = Handles.FreeMoveHandle(_poles[_selected], poleSize, Vector3.zero, Handles.SphereHandleCap);
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(this, "Pose Picker: IK");
                    _poles[_selected] = pole;
                    SolveTwoBone(upper, lower, selected, target, pole);
                    StoreFromPose(limb[0]);
                    StoreFromPose(limb[1]);
                    StoreFromPose(_selected);
                    Sample();
                    Repaint();
                }
            }
        }

        /// <summary>
        /// The sample aim as an arrow from the chest: the direction the action aims at, in the body's
        /// own frame (forward with the sample pitch). Dragging its tip changes the sample pitch.
        /// </summary>
        private void DrawAimArrow()
        {
            Transform chest = _previewAnimator.GetBoneTransform(HumanBodyBones.Chest) ?? _previewHips;
            if (chest == null)
                return;
            Quaternion frame = BodyFrame();
            Vector3 direction = frame * Quaternion.Euler(_sampleAim, 0f, 0f) * Vector3.forward;
            Vector3 origin = chest.position;
            float length = HandleUtility.GetHandleSize(origin) * 1.2f;
            Handles.color = new Color(0.3f, 0.85f, 1f);
            Handles.ArrowHandleCap(0, origin, Quaternion.LookRotation(direction), length, EventType.Repaint);

            Vector3 tip = origin + direction * length;
            EditorGUI.BeginChangeCheck();
            Vector3 moved = Handles.FreeMoveHandle(tip, HandleUtility.GetHandleSize(tip) * 0.06f, Vector3.zero, Handles.SphereHandleCap);
            if (EditorGUI.EndChangeCheck())
            {
                Vector3 local = Quaternion.Inverse(frame) * (moved - origin);
                Undo.RecordObject(this, "Pose Picker: sample aim");
                _sampleAim = Mathf.Clamp(-Mathf.Atan2(local.y, local.z) * Mathf.Rad2Deg, -89f, 89f);
                ShowPose();
                Sample();
                Repaint();
            }
        }

        /// <summary>The body's unturned frame: the model's base rotation in the world.</summary>
        private Quaternion BodyFrame()
        {
            Transform parent = _previewModel.parent;
            return (parent != null ? parent.rotation : Quaternion.identity) * _previewBaseRotation;
        }

        /// <summary>
        /// Moves the end of a two-bone chain to the target, the root staying put: the triangle's sides
        /// (upper and lower length, root-to-target distance) give the elbow's angle (law of cosines),
        /// the pole picks which side it bends to. Out of reach, the chain points straight at the target.
        /// The end keeps its angle in the world.
        /// </summary>
        private static void SolveTwoBone(Transform upper, Transform lower, Transform end, Vector3 target, Vector3 pole)
        {
            Quaternion endRotation = end.rotation;
            Vector3 a = upper.position, b = lower.position, c = end.position;
            float l1 = (b - a).magnitude, l2 = (c - b).magnitude;
            Vector3 toTarget = target - a;
            Vector3 dir = toTarget.sqrMagnitude > 1e-8f ? toTarget.normalized : (c - a).normalized;
            float dist = Mathf.Clamp(toTarget.magnitude, Mathf.Abs(l1 - l2) + 1e-3f, l1 + l2 - 1e-3f);

            Vector3 bend = Vector3.ProjectOnPlane(pole - a, dir);
            if (bend.sqrMagnitude < 1e-8f)
                bend = Vector3.ProjectOnPlane(b - a, dir);
            if (bend.sqrMagnitude < 1e-8f)
                bend = Vector3.ProjectOnPlane(Vector3.up, dir);
            bend.Normalize();

            float cosA = Mathf.Clamp((l1 * l1 + dist * dist - l2 * l2) / (2f * l1 * dist), -1f, 1f);
            float sinA = Mathf.Sqrt(1f - cosA * cosA);
            Vector3 elbow = a + l1 * (dir * cosA + bend * sinA);

            upper.rotation = Quaternion.FromToRotation(b - a, elbow - a) * upper.rotation;
            Vector3 endGoal = a + dir * dist;
            lower.rotation = Quaternion.FromToRotation(end.position - lower.position, endGoal - lower.position) * lower.rotation;
            end.rotation = endRotation;
        }

        private static Vector3 DefaultPole(Transform upper, Transform lower, Transform end)
        {
            Vector3 middle = (upper.position + end.position) * 0.5f;
            Vector3 outward = lower.position - middle;
            if (outward.sqrMagnitude < 1e-6f)
                outward = lower.forward;
            return lower.position + outward.normalized * 0.3f;
        }

        // --- Offsets ----------------------------------------------------------------------------

        /// <summary>Records the bone's current preview rotation as its offset from the clip's rotation.</summary>
        private void StoreFromPose(HumanBodyBones id)
        {
            Transform bone = _previewAnimator.GetBoneTransform(id);
            if (bone != null && _animatedLocal.TryGetValue(id, out Quaternion animated))
                SetOffset(id, Quaternion.Inverse(animated) * bone.localRotation);
        }

        private Quaternion OffsetFor(HumanBodyBones id)
        {
            foreach (PoseOffset.BoneOffset offset in _bones)
                if (offset.Bone == id)
                    return offset.Rotation;
            return Quaternion.identity;
        }

        private void SetOffset(HumanBodyBones id, Quaternion rotation)
        {
            int index = _bones.FindIndex(b => b.Bone == id);
            if (Quaternion.Angle(rotation, Quaternion.identity) < 0.01f)
            {
                if (index >= 0)
                    _bones.RemoveAt(index);
                return;
            }
            var offset = new PoseOffset.BoneOffset { Bone = id, Rotation = rotation };
            if (index >= 0)
                _bones[index] = offset;
            else
                _bones.Add(offset);
        }

        private static IEnumerable<HumanBodyBones> LimbEnds()
        {
            foreach (HumanBodyBones[] limb in Limbs)
                yield return limb[2];
        }

        private static bool IsLimbEnd(HumanBodyBones id, out HumanBodyBones[] limb)
        {
            foreach (HumanBodyBones[] chain in Limbs)
                if (chain[2] == id)
                {
                    limb = chain;
                    return true;
                }
            limb = null;
            return false;
        }

        private static Vector3 Signed(Vector3 euler) =>
            new(Mathf.DeltaAngle(0f, euler.x), Mathf.DeltaAngle(0f, euler.y), Mathf.DeltaAngle(0f, euler.z));

        // --- States -----------------------------------------------------------------------------

        private void SetController(AnimatorController controller)
        {
            _controller = controller;
            _states.Clear();
            _stateNames.Clear();
            _stateIndex = 0;
            _clip = null;
            if (controller == null)
                return;
            foreach (AnimatorControllerLayer layer in controller.layers)
                CollectStates(layer.stateMachine, layer.name + "/");
            if (_states.Count > 0)
                LoadFrom(_states[0]);
        }

        private void LoadFrom(AnimatorState state)
        {
            PoseOffset saved = OffsetOf(state);
            _rotation = saved != null ? saved.Rotation : Vector3.zero;
            _bones = saved != null ? new List<PoseOffset.BoneOffset>(saved.Bones) : new List<PoseOffset.BoneOffset>();
            _followAim = saved != null && saved.FollowAim;
            _blendIn = saved != null ? saved.BlendIn : 0.1f;
            _blendShape = saved != null && saved.BlendShape != null && saved.BlendShape.length > 0
                ? new AnimationCurve(saved.BlendShape.keys) : AnimationCurve.Linear(0f, 0f, 1f, 1f);
            _mask = new BoneMask();
            _poles.Clear();
            _selected = HumanBodyBones.LastBone;
        }

        private static PoseOffset OffsetOf(AnimatorState state)
        {
            foreach (StateMachineBehaviour behaviour in state.behaviours)
                if (behaviour is PoseOffset offset)
                    return offset;
            return null;
        }

        private void CollectStates(AnimatorStateMachine machine, string path)
        {
            foreach (ChildAnimatorState child in machine.states)
            {
                _states.Add(child.state);
                _stateNames.Add(path + child.state.name);
            }
            foreach (ChildAnimatorStateMachine sub in machine.stateMachines)
                CollectStates(sub.stateMachine, path + sub.stateMachine.name + "/");
        }

        private void Apply(AnimatorState state, float normalized)
        {
            EnsureLoops(_clip);
            Undo.RecordObject(state, "Pose Picker: hold state at pose");
            state.motion = _clip;
            state.speed = 0f;
            state.cycleOffset = normalized;
            state.cycleOffsetParameterActive = false;

            PoseOffset offset = OffsetOf(state) ?? state.AddStateMachineBehaviour<PoseOffset>();
            Undo.RecordObject(offset, "Pose Picker: pose settings");
            offset.Rotation = _rotation;
            offset.Bones = new List<PoseOffset.BoneOffset>(_bones);
            offset.FollowAim = _followAim;
            offset.BlendIn = _blendIn;
            offset.BlendShape = new AnimationCurve(_blendShape.keys);
            EditorUtility.SetDirty(offset);
            EditorUtility.SetDirty(state);
            int zeroed = ZeroIncomingTransitions(state);
            AssetDatabase.SaveAssets();
            Debug.Log($"Pose Picker: {state.name} = {_clip.name}, frame {Mathf.RoundToInt(_time * _clip.frameRate)}, " +
                      $"normalized {normalized:0.000}, rotation {_rotation}, {_bones.Count} bones, aim {(_followAim ? "on" : "off")}, " +
                      $"blend {_blendIn:0.##} s, {zeroed} incoming transitions set to 0 s");
        }

        /// <summary>
        /// The pose blends itself in (ModelPose); an Animator cross-fade into the state on top would blend
        /// twice. Every transition into the state becomes 0 s.
        /// </summary>
        private int ZeroIncomingTransitions(AnimatorState target)
        {
            int count = 0;
            if (_controller == null)
                return count;
            foreach (AnimatorControllerLayer layer in _controller.layers)
                count += ZeroIn(layer.stateMachine, target);
            return count;
        }

        private static int ZeroIn(AnimatorStateMachine machine, AnimatorState target)
        {
            int count = 0;
            var transitions = new List<AnimatorStateTransition>(machine.anyStateTransitions);
            foreach (ChildAnimatorState child in machine.states)
                transitions.AddRange(child.state.transitions);
            foreach (AnimatorStateTransition transition in transitions)
                if (transition.destinationState == target && (transition.duration > 0f || !transition.hasFixedDuration))
                {
                    Undo.RecordObject(transition, "Pose Picker: transition");
                    transition.hasFixedDuration = true;
                    transition.duration = 0f;
                    EditorUtility.SetDirty(transition);
                    count++;
                }
            foreach (ChildAnimatorStateMachine sub in machine.stateMachines)
                count += ZeroIn(sub.stateMachine, target);
            return count;
        }

        /// <summary>
        /// A state's Cycle Offset only takes effect on a looping clip; on any other the state shows the
        /// first frame. Turns Loop Time on for the clip's take in its model importer (Loop Pose off, so
        /// the pose itself is untouched).
        /// </summary>
        private static void EnsureLoops(AnimationClip clip)
        {
            string path = AssetDatabase.GetAssetPath(clip);
            if (AssetImporter.GetAtPath(path) is not ModelImporter importer)
                return;
            ModelImporterClipAnimation[] takes = importer.clipAnimations;
            bool changed = false;
            for (int i = 0; i < takes.Length; i++)
                if (takes[i].name == clip.name && !takes[i].loopTime)
                {
                    takes[i].loopTime = true;
                    takes[i].loopPose = false;
                    changed = true;
                }
            if (!changed)
                return;
            importer.clipAnimations = takes;
            importer.SaveAndReimport();
            Debug.Log($"Pose Picker: turned Loop Time on for {clip.name} (otherwise the state would show the first frame).");
        }

        // --- Preview ----------------------------------------------------------------------------

        private bool IsPreviewing => _previewAnimator != null;

        /// <summary>Leaves the blend preview: the finished pose shows again.</summary>
        private void ShowPose()
        {
            _showBlend = false;
            _playing = false;
        }

        /// <summary>
        /// Unity can leave animation mode on its own (another tool, an asset reimport); the preview would
        /// then freeze in its last pose and ignore the frame slider. Back into it, or clean up if the copy
        /// is gone.
        /// </summary>
        private void KeepPreviewAlive()
        {
            if (_previewAnimator == null)
            {
                if (_previewModel != null || _previewCopy != null)
                    StopPreview();
                return;
            }
            if (!AnimationMode.InAnimationMode() && !EditorApplication.isPlayingOrWillChangePlaymode)
                AnimationMode.StartAnimationMode();
        }

        // The preview timeline: the previous animation from its first chosen frame to the second, the
        // blend, then a short hold on the finished pose.
        private float LeadLength => _fromClip == null ? 0f
            : _fromTime >= _leadStart ? _fromTime - _leadStart : _fromClip.length - _leadStart + _fromTime;

        private float TimelineLength => LeadLength + _blendIn + Hold;

        private bool BlendVisible => _showBlend && _fromClip != null && _timeline < LeadLength + _blendIn;

        private void StartPreview()
        {
            if (EditorApplication.isPlaying)
            {
                ShowNotification(new GUIContent("No preview in Play Mode"));
                return;
            }
            StopPreview();
            // Copies left behind by an earlier session (a domain reload forgets them) go first.
            foreach (GameObject leftover in Resources.FindObjectsOfTypeAll<GameObject>())
                if (leftover != null && leftover.name == "PosePicker Preview" && leftover.scene.IsValid())
                    DestroyImmediate(leftover);
            GameObject target = _model;
            // An asset gets a temporary scene copy, never saved, removed when the preview stops.
            if (EditorUtility.IsPersistent(_model))
            {
                _previewCopy = (GameObject)PrefabUtility.InstantiatePrefab(_model);
                if (_previewCopy == null)
                    _previewCopy = Instantiate(_model);
                _previewCopy.name = "PosePicker Preview";
                _previewCopy.hideFlags = HideFlags.DontSave;
                if (SceneView.lastActiveSceneView != null)
                    _previewCopy.transform.position = SceneView.lastActiveSceneView.pivot;
                target = _previewCopy;
            }
            _previewAnimator = target.GetComponentInChildren<Animator>();
            if (_previewAnimator == null)
            {
                ShowNotification(new GUIContent("The model has no Animator"));
                StopPreview();
                return;
            }
            // Root motion off, as in game: the clip's travel is left out and the pose stays in place.
            // Only on our own copy; a scene object keeps its own setting.
            if (_previewCopy != null)
                _previewAnimator.applyRootMotion = false;
            _previewModel = _previewAnimator.transform;
            _previewHips = _previewAnimator.isHuman ? _previewAnimator.GetBoneTransform(HumanBodyBones.Hips) : null;
            _previewBasePosition = _previewModel.localPosition;
            _previewBaseRotation = _previewModel.localRotation;
            _previewSnapshot = new PoseSnapshot(_previewAnimator);
            AnimationMode.StartAnimationMode();
            if (_previewCopy != null && SceneView.lastActiveSceneView != null)
                SceneView.lastActiveSceneView.Frame(new Bounds(_previewCopy.transform.position + Vector3.up, Vector3.one * 2.5f), false);
        }

        private void StopPreview()
        {
            _playing = false;
            _layerPreview = LayerPreview.Off;
            if (_baseGraph.IsValid())
                _baseGraph.Destroy();
            _baseGraphClip = null;
            if (_graph.IsValid())
                _graph.Destroy();
            if (_fromGraph.IsValid())
                _fromGraph.Destroy();
            _graphClip = null;
            _fromGraphClip = null;
            // Animation mode puts the sampled bones back, including our turns on them.
            if (AnimationMode.InAnimationMode())
                AnimationMode.StopAnimationMode();
            // A scene object gets its own pose back; the rotation preview moved it.
            if (_previewModel != null && _previewCopy == null)
                _previewModel.SetLocalPositionAndRotation(_previewBasePosition, _previewBaseRotation);
            _previewModel = null;
            _previewHips = null;
            _previewSnapshot = null;
            if (_previewCopy != null)
                DestroyImmediate(_previewCopy);
            _previewCopy = null;
            _previewAnimator = null;
            _animatedLocal.Clear();
            _selected = HumanBodyBones.LastBone;
            SceneView.RepaintAll();
        }

        private void SampleClip(ref PlayableGraph graph, ref AnimationClip graphClip, AnimationClip clip, float time)
        {
            if (!graph.IsValid() || graphClip != clip)
            {
                if (graph.IsValid())
                    graph.Destroy();
                graph = PlayableGraph.Create("PosePicker");
                AnimationPlayableOutput output = AnimationPlayableOutput.Create(graph, "Pose", _previewAnimator);
                output.SetSourcePlayable(AnimationClipPlayable.Create(graph, clip));
                graphClip = clip;
            }
            AnimationMode.BeginSampling();
            AnimationMode.SamplePlayableGraph(graph, 0, time);
            AnimationMode.EndSampling();
        }

        /// <summary>
        /// Builds the preview the way ModelPose builds the game frame: the clip's pose, bone turns on it,
        /// the body around the hips (with the sample aim if the pose follows it). Showing the blend, it
        /// first takes the 'from' clip's frame as a snapshot and puts the body between the two by the
        /// curve, as ModelPose does on entering the state.
        /// </summary>
        private void Sample()
        {
            KeepPreviewAlive();
            if (_clip == null || !IsPreviewing || !AnimationMode.InAnimationMode())
                return;
            if (Layered)
            {
                SampleLayered();
                return;
            }
            bool blend = BlendVisible;
            float weight = 1f;
            if (blend)
            {
                float lead = LeadLength;
                if (_timeline < lead)
                {
                    // Before the blend: only the previous animation, playing on.
                    float time = _leadStart + _timeline;
                    if (time > _fromClip.length)
                        time -= _fromClip.length;
                    SampleClip(ref _fromGraph, ref _fromGraphClip, _fromClip, time);
                    _previewModel.SetLocalPositionAndRotation(_previewBasePosition, _previewBaseRotation);
                    SceneView.RepaintAll();
                    return;
                }
                // As in game: the body as it stood when the pose began, frozen, blended into the pose.
                SampleClip(ref _fromGraph, ref _fromGraphClip, _fromClip, _fromTime);
                _previewModel.SetLocalPositionAndRotation(_previewBasePosition, _previewBaseRotation);
                _previewSnapshot.Capture();
                weight = BlendShapes.Evaluate(_blendShape, _blendIn > 0f ? (_timeline - lead) / _blendIn : 1f);
            }

            SamplePose();
            Quaternion rotation = Quaternion.Euler(_rotation);
            if (_followAim)
                rotation = Quaternion.Euler(_sampleAim, 0f, 0f) * rotation;
            ModelPose.Apply(_previewModel, _previewHips, _previewBasePosition, _previewBaseRotation, rotation);

            if (blend)
                _previewSnapshot.BlendTo(weight);
            SceneView.RepaintAll();
        }

        // --- Mask (layered by bone) -------------------------------------------------------------

        /// <summary>The pose takes only some bones: played from code over the Animator, not held by a state.</summary>
        private bool Layered => _mask != null && !_mask.IsWholeBody;

        private static readonly string[] AllBoneNames = BuildBoneNames();

        private static string[] BuildBoneNames()
        {
            var names = new string[(int)HumanBodyBones.LastBone];
            for (int i = 0; i < names.Length; i++)
                names[i] = ObjectNames.NicifyVariableName(((HumanBodyBones)i).ToString());
            return names;
        }

        private void DrawMaskSection()
        {
            _foldMask = EditorGUILayout.BeginFoldoutHeaderGroup(_foldMask, "3. Mask (layered by bone)");
            if (_foldMask)
            {
                EditorGUILayout.LabelField(Layered
                        ? "Only these bones take the pose; the rest keeps whatever the Animator plays. Grey dots in the Scene view are outside the mask."
                        : "Whole body. Add a branch and only that bone and everything under it take the pose (e.g. upper body over a run).",
                    EditorStyles.wordWrappedMiniLabel);

                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Whole body", EditorStyles.miniButtonLeft))
                        SetMask();
                    if (GUILayout.Button("Upper body", EditorStyles.miniButtonMid))
                        SetMask(new BoneMask.Branch { Bone = HumanBodyBones.Spine, BlendDepth = 2 });
                    if (GUILayout.Button("Arms", EditorStyles.miniButtonMid))
                        SetMask(new BoneMask.Branch { Bone = HumanBodyBones.LeftShoulder },
                            new BoneMask.Branch { Bone = HumanBodyBones.RightShoulder });
                    if (GUILayout.Button("Head", EditorStyles.miniButtonRight))
                        SetMask(new BoneMask.Branch { Bone = HumanBodyBones.Neck });
                }

                for (int i = 0; i < _mask.Branches.Count; i++)
                {
                    BoneMask.Branch branch = _mask.Branches[i];
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUI.BeginChangeCheck();
                        int bone = EditorGUILayout.Popup((int)branch.Bone, AllBoneNames);
                        EditorGUIUtility.labelWidth = 40;
                        int depth = EditorGUILayout.IntField(new GUIContent("Depth",
                            "Bones down the branch over which the pose fades in. 0: full strength from this bone."), branch.BlendDepth, GUILayout.Width(80));
                        EditorGUIUtility.labelWidth = 0;
                        bool exclude = GUILayout.Toggle(branch.Exclude, new GUIContent("Exclude", "Takes this branch out of the pose."),
                            EditorStyles.miniButton, GUILayout.Width(60));
                        if (EditorGUI.EndChangeCheck())
                        {
                            Undo.RecordObject(this, "Pose Picker: mask branch");
                            _mask.Branches[i] = new BoneMask.Branch { Bone = (HumanBodyBones)bone, BlendDepth = Mathf.Max(0, depth), Exclude = exclude };
                        }
                        if (GUILayout.Button("X", EditorStyles.miniButton, GUILayout.Width(22)))
                        {
                            Undo.RecordObject(this, "Pose Picker: remove branch");
                            _mask.Branches.RemoveAt(i);
                            GUIUtility.ExitGUI();
                        }
                    }
                }

                bool picked = _selected != HumanBodyBones.LastBone;
                if (GUILayout.Button(picked ? $"Add branch at {BoneNames[_selected]}" : "Add branch (pick a bone dot to start it there)"))
                {
                    Undo.RecordObject(this, "Pose Picker: add branch");
                    _mask.Branches.Add(new BoneMask.Branch { Bone = picked ? _selected : HumanBodyBones.Spine });
                }

                if (!Layered)
                    _layerPreview = LayerPreview.Off;
                else
                {
                    EditorGUILayout.Space(4);
                    EditorGUILayout.LabelField("Preview: animation underneath", EditorStyles.miniBoldLabel);
                    _baseClip = (AnimationClip)EditorGUILayout.ObjectField(new GUIContent("Clip",
                        "Plays in a loop under the pose, as the Animator would in game (e.g. a run)."), _baseClip, typeof(AnimationClip), false);
                    GUI.enabled = _baseClip != null && _model != null;
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        if (GUILayout.Toggle(_layerPreview == LayerPreview.Hold, "Play with pose held", EditorStyles.miniButtonLeft)
                            != (_layerPreview == LayerPreview.Hold))
                            SetLayerPreview(_layerPreview == LayerPreview.Hold ? LayerPreview.Off : LayerPreview.Hold);
                        if (GUILayout.Toggle(_layerPreview == LayerPreview.Blend, "Play blend in and out", EditorStyles.miniButtonRight)
                            != (_layerPreview == LayerPreview.Blend))
                            SetLayerPreview(_layerPreview == LayerPreview.Blend ? LayerPreview.Off : LayerPreview.Blend);
                    }
                    GUI.enabled = true;
                    if (_layerPreview == LayerPreview.Blend)
                        EditorGUILayout.LabelField(" ", LayerPhase(), EditorStyles.miniLabel);
                }
            }
            EditorGUILayout.EndFoldoutHeaderGroup();
        }

        private void SetMask(params BoneMask.Branch[] branches)
        {
            Undo.RecordObject(this, "Pose Picker: mask");
            _mask = new BoneMask { Branches = new List<BoneMask.Branch>(branches) };
        }

        private void SetLayerPreview(LayerPreview mode)
        {
            if (mode != LayerPreview.Off && !IsPreviewing)
                StartPreview();
            _layerPreview = IsPreviewing ? mode : LayerPreview.Off;
            _baseTime = 0f;
            _baseTick = EditorApplication.timeSinceStartup;
            if (IsPreviewing)
                Sample();
        }

        private float LayerCycle => LayerRest + _blendIn + LayerHold + _blendOut + LayerRest;

        /// <summary>
        /// The pose's strength in the layered preview. Held: full. Blend: a loop of the animation alone, the
        /// pose blending in along the curve, held, blending out, the animation alone again, as
        /// ModelPose.PlayLayered and StopLayered do in game.
        /// </summary>
        private float LayerPreviewWeight()
        {
            if (_layerPreview == LayerPreview.Hold)
                return 1f;
            float t = Mathf.Repeat(_baseTime, LayerCycle) - LayerRest;
            if (t < 0f)
                return 0f;
            if (t < _blendIn)
                return BlendShapes.Evaluate(_blendShape, t / _blendIn);
            t -= _blendIn;
            if (t < LayerHold)
                return 1f;
            t -= LayerHold;
            if (t < _blendOut)
                return BlendShapes.FadeOut(t / _blendOut);
            return 0f;
        }

        private string LayerPhase()
        {
            float t = Mathf.Repeat(_baseTime, LayerCycle) - LayerRest;
            if (t < 0f)
                return "Animation alone";
            if (t < _blendIn)
                return "Blending in";
            if (t < _blendIn + LayerHold)
                return "Pose held";
            if (t < _blendIn + LayerHold + _blendOut)
                return "Blending out";
            return "Animation alone";
        }

        /// <summary>The picked frame with the bone turns on, as the bones' local rotations (whole body).</summary>
        private Quaternion[] PosedRotations()
        {
            SamplePose();
            var rotations = new Quaternion[(int)HumanBodyBones.LastBone];
            for (int i = 0; i < rotations.Length; i++)
            {
                Transform bone = _previewAnimator.GetBoneTransform((HumanBodyBones)i);
                rotations[i] = bone != null ? bone.localRotation : Quaternion.identity;
            }
            return rotations;
        }

        /// <summary>
        /// For Save: every bone's local rotation in the finished pose, read on the preview model (a
        /// short preview runs if none is on). Null without a humanoid model.
        /// </summary>
        private List<PoseAsset.BoneRotation> BakeRotations()
        {
            if (_model == null || _clip == null)
                return null;
            bool started = !IsPreviewing;
            if (started)
                StartPreview();
            if (!IsPreviewing || !_previewAnimator.isHuman)
            {
                if (started)
                    StopPreview();
                return null;
            }
            KeepPreviewAlive();
            Quaternion[] posed = PosedRotations();
            var rotations = new List<PoseAsset.BoneRotation>();
            for (int i = 0; i < posed.Length; i++)
                if (_previewAnimator.GetBoneTransform((HumanBodyBones)i) != null)
                    rotations.Add(new PoseAsset.BoneRotation { Bone = (HumanBodyBones)i, Rotation = posed[i] });
            if (started)
                StopPreview();
            else
                Sample();
            return rotations;
        }

        /// <summary>
        /// The layered preview, built the way ModelPose builds the game frame: the animation underneath on
        /// the whole body, then the masked bones turned toward the pose by the mask times the pose's
        /// strength. With nothing playing underneath it shows the pose alone, for editing.
        /// </summary>
        private void SampleLayered()
        {
            Quaternion[] posed = PosedRotations();
            if (_layerPreview != LayerPreview.Off && _baseClip != null)
            {
                SampleClip(ref _baseGraph, ref _baseGraphClip, _baseClip, Mathf.Repeat(_baseTime, Mathf.Max(0.01f, _baseClip.length)));
                new PoseLayer(_previewAnimator, _mask, posed).Apply(LayerPreviewWeight());
            }
            _previewModel.SetLocalPositionAndRotation(_previewBasePosition, _previewBaseRotation);
            SceneView.RepaintAll();
        }

        /// <summary>The picked frame on the preview, its animated rotations kept (offsets are measured from them), bone turns on.</summary>
        private void SamplePose()
        {
            SampleClip(ref _graph, ref _graphClip, _clip, _time);
            _animatedLocal.Clear();
            if (_previewAnimator.isHuman)
                foreach (HumanBodyBones id in EditableBones)
                {
                    Transform bone = _previewAnimator.GetBoneTransform(id);
                    if (bone != null)
                        _animatedLocal[id] = bone.localRotation;
                }
            ModelPose.ApplyBones(_previewAnimator, _bones);
        }
    }
}
