using UnityEditor;
using UnityEngine;

namespace FastScriptReload.Editor.NewFields
{
    /// <summary>
    /// Shows fields added by hot reload for the components of the selected GameObject.
    /// Needed where they can't be drawn in the inspector, as that relies on Harmony patches (eg Apple Silicon).
    /// </summary>
    public class NewFieldsWindow : EditorWindow
    {
        public const string MenuPath = "Window/Fast Script Reload/Added Fields";

        private Vector2 _scrollPosition;

        [MenuItem(MenuPath, false, 1999)]
        public static void Open()
        {
            GetWindow<NewFieldsWindow>("Added Fields");
        }

        private void OnSelectionChange()
        {
            Repaint();
        }

        //Called 10 times per second, keeps values changed by running code up to date
        private void OnInspectorUpdate()
        {
            Repaint();
        }

        private void OnGUI()
        {
            var selectedGameObject = Selection.activeGameObject;
            if (!selectedGameObject)
            {
                EditorGUILayout.HelpBox("Select a GameObject to see fields added to its components by hot reload.", MessageType.Info);
                return;
            }

            _scrollPosition = EditorGUILayout.BeginScrollView(_scrollPosition);
            var isAnyComponentWithAddedFields = false;
            foreach (var component in selectedGameObject.GetComponents<Component>())
            {
                if (!NewFieldsRendererDefaultEditorPatch.HasNewlyAddedFields(component))
                {
                    continue;
                }

                isAnyComponentWithAddedFields = true;
                EditorGUILayout.LabelField(component.GetType().Name, EditorStyles.boldLabel);
                NewFieldsRendererDefaultEditorPatch.RenderNewlyAddedFields(component);
                EditorGUILayout.Space(10);
            }
            EditorGUILayout.EndScrollView();

            if (!isAnyComponentWithAddedFields)
            {
                EditorGUILayout.HelpBox($"No fields added by hot reload on '{selectedGameObject.name}'. Added fields show up once they've been read by the running code.", MessageType.Info);
            }
        }
    }
}
