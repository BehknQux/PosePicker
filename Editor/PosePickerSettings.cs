using UnityEditor;
using UnityEngine;

namespace PosePicker.Editor
{
    /// <summary>
    /// Project-wide Pose Picker settings, kept in ProjectSettings/PosePickerSettings.asset so they go into
    /// version control with the project. Set from the window's Library section.
    /// </summary>
    [FilePath("ProjectSettings/PosePickerSettings.asset", FilePathAttribute.Location.ProjectFolder)]
    public class PosePickerSettings : ScriptableSingleton<PosePickerSettings>
    {
        [Tooltip("Folder under Assets where saved poses go.")]
        [SerializeField] private string _poseFolder = "Assets/Poses";

        public string PoseFolder
        {
            get => _poseFolder;
            set
            {
                if (_poseFolder == value)
                    return;
                _poseFolder = value;
                Save(true);
            }
        }

        /// <summary>Makes the pose folder and any missing parents.</summary>
        public void EnsurePoseFolder()
        {
            string[] parts = _poseFolder.Split('/');
            string path = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                string next = $"{path}/{parts[i]}";
                if (!AssetDatabase.IsValidFolder(next))
                    AssetDatabase.CreateFolder(path, parts[i]);
                path = next;
            }
        }
    }
}
