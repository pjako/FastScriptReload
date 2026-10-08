using System.Collections.Generic;
using ImmersiveVRTools.Editor.Common.WelcomeScreen;
using ImmersiveVRTools.Editor.Common.WelcomeScreen.PreferenceDefinition;
using UnityEditor;
using UnityEngine;

namespace FastScriptReload.Editor
{
    /// <summary>Settings page in 'Project Settings -> Fast Script Reload'</summary>
    public class FastScriptReloadSettingsProvider : SettingsProvider
    {
        public const string SettingsPath = "Project/Fast Script Reload";
        private const float LabelWidth = 420;

        private static readonly HashSet<string> Keywords = new HashSet<string> { "hot reload", "script", "play mode", "fast script reload" };

        private bool _isAdvancedExpanded;

        private FastScriptReloadSettingsProvider() : base(SettingsPath, SettingsScope.Project, Keywords)
        {
        }

        [SettingsProvider]
        public static SettingsProvider Create()
        {
            return new FastScriptReloadSettingsProvider();
        }

        public static void Open()
        {
            SettingsService.OpenProjectSettings(SettingsPath);
        }

        public override void OnGUI(string searchContext)
        {
            var previousLabelWidth = EditorGUIUtility.labelWidth;
            EditorGUIUtility.labelWidth = LabelWidth;
            try
            {
                EditorGUILayout.HelpBox("Changed scripts are hot reloaded while playing. Outside play mode Unity compiles them as usual, " +
                                        "saving a script starts the compilation right away.", MessageType.Info);
                Render(FastScriptReloadPreference.EnableAutoReloadForChangedFiles);
                Render(FastScriptReloadPreference.KeepPlayModeRunningInBackground);

                EditorGUILayout.Space(10);
                EditorGUILayout.LabelField("Excluded Scripts", EditorStyles.boldLabel);
                EditorGUILayout.HelpBox("Right-click a script in the Project window, then 'Fast Script Reload -> Add / Remove Hot-Reload Exclusion'.", MessageType.None);
                Render(FastScriptReloadPreference.FilesExcludedFromHotReload);

                EditorGUILayout.Space(10);
                _isAdvancedExpanded = EditorGUILayout.Foldout(_isAdvancedExpanded, "Advanced", true);
                if (_isAdvancedExpanded)
                {
                    RenderAdvanced();
                }
            }
            finally
            {
                EditorGUIUtility.labelWidth = previousLabelWidth;
            }
        }

        private static void RenderAdvanced()
        {
            using (new EditorGUI.IndentLevelScope())
            {
                Render(FastScriptReloadPreference.ReloadAfterChangesSettleForNMilliseconds);
                Render(FastScriptReloadPreference.EnableOnDemandReload);

                Render(FastScriptReloadPreference.EnableExperimentalEditorHotReloadSupport);
                if ((bool)FastScriptReloadPreference.EnableExperimentalEditorHotReloadSupport.GetEditorPersistedValueOrDefault())
                {
                    EditorGUILayout.HelpBox("Hot reloads instead of letting Unity compile. Unity's serialization and inspectors don't see the changes " +
                                            "and some changes still need a compile, it's less reliable than reloading in play mode.", MessageType.Warning);
                }

                Render(FastScriptReloadPreference.IsVisualHotReloadIndicationShownInProjectWindow);
                Render(FastScriptReloadPreference.IsAutoOpenGeneratedSourceFileOnChangeEnabled);
                Render(FastScriptReloadPreference.EnableDetailedDebugLogging);
                Render(FastScriptReloadPreference.ShowStartScreenOnStartup);

                Render(FastScriptReloadPreference.FileWatcherImplementationInUse);
                EditorGUILayout.HelpBox("UnityDefault fits most projects. CustomPolling checks files for changes regularly, use it if changes aren't picked up " +
                                        "(eg on network drives). Applies after the next domain reload.", MessageType.None);

                EditorGUILayout.Space(5);
                EditorGUILayout.HelpBox("Hot reload compiles against all assemblies the project uses. If one of them causes compilation errors, exclude it here.", MessageType.None);
                Render(FastScriptReloadPreference.ReferencesExcludedFromHotReload);
            }
        }

        private static void Render(ProjectEditorPreferenceDefinitionBase preference)
        {
            ProductPreferenceBase.RenderGuiAndPersistInput(preference);
        }
    }
}
