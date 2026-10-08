using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FastScriptReload.Editor.Compilation;
using FastScriptReload.Editor.Compilation.ScriptGenerationOverrides;
using FastScriptReload.Runtime;
using ImmersiveVRTools.Editor.Common.Utilities;
using ImmersiveVRTools.Editor.Common.WelcomeScreen;
using ImmersiveVRTools.Editor.Common.WelcomeScreen.GuiElements;
using ImmersiveVRTools.Editor.Common.WelcomeScreen.PreferenceDefinition;
using ImmersiveVRTools.Editor.Common.WelcomeScreen.Utilities;
using ImmersiveVrToolsCommon.Runtime.Logging;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.Rendering;

namespace FastScriptReload.Editor
{
    public class FastScriptReloadWelcomeScreen : ProductWelcomeScreenBase 
    {
        public static string BaseUrl = "https://immersivevrtools.com";
        public static string GenerateGetUpdatesUrl(string userId, string versionId)
        {
            //WARN: the URL can sometimes be adjusted, make sure updated correctly
            return $"{BaseUrl}/updates/fast-script-reload/{userId}?CurrentVersion={versionId}";
        }
        public static string VersionId = "2.0";
        private static readonly string ProjectIconName = "ProductIcon64";
        public static readonly string ProjectName = "fast-script-reload";

        private static Vector2 _WindowSizePx = new Vector2(650, 500);
        private static string _WindowTitle = "Fast Script Reload";

        public static ChangeMainViewButton UserScriptRewriteOverrides { get; private set; }
        public static ChangeMainViewButton InspectError { get; private set; }

        public static DynamicFileHotReloadState LastInspectFileHotReloadStateError;

        public void OpenInspectError(DynamicFileHotReloadState fileHotReloadState)
        {
            LastInspectFileHotReloadStateError = fileHotReloadState;
            InspectError.OnClick(this);
        }
        
        public void OpenUserScriptRewriteOverridesSection()
        {
            UserScriptRewriteOverrides.OnClick(this);
        }
        
        private static readonly ScrollViewGuiSection MainScrollViewSection = new ScrollViewGuiSection(
            "", (screen) =>
            {
                GUILayout.Label(
@"Thanks for using the asset! If at any stage you got some questions or need help please let me know.

Asset is released as open source and I'd like to dedicate as much time to it as possible.
For that to happen though it needs a community around it. It's HUGE help if you can:

1) Spread the word, let other devs know how you're using it
2) Star Github Repo - it helps build visibility
3) Donate - this allows me to spend more time on the project instead of paid client's work

Settings are in Project Settings -> Fast Script Reload.

You can always get back to this screen via Window -> Fast Script Reload -> Start Screen", screen.TextStyle, GUILayout.ExpandHeight(true));

                if (GUILayout.Button("Open Settings"))
                {
                    FastScriptReloadSettingsProvider.Open();
                }
            }
        );

        private static readonly List<GuiSection> LeftSections = CreateLeftSections(new List<ChangeMainViewButton>
            {
                new ChangeMainViewButton("On-Device\r\nHot-Reload",  
                    (screen) =>
                    {
                        EditorGUILayout.LabelField("Live Script Reload", screen.BoldTextStyle); 
                        
                        GUILayout.Space(10);
                        EditorGUILayout.LabelField(@"There's an extension to this asset that'll allow you to include Hot-Reload capability in builds (standalone / Android), please click the button below to learn more.", screen.TextStyle);

                        GUILayout.Space(20);
                        if (GUILayout.Button("View Live Script Reload on Asset Store"))
                        {
                            Application.OpenURL($"{RedirectBaseUrl}/live-script-reload-extension");
                        }
                    }
                )
            }, 
            new LaunchSceneButton("Basic Example", (s) =>
            {
                var path = GetScenePath("ExampleScene");
                if (path == null)
                {
                    var userChoice = EditorUtility.DisplayDialogComplex("Example not found",
                        "Example scene was not found. If you got FSR via package manager, please make sure to import samples.", 
                        "Ok", "Close", "Open Package Manager");
                    if (userChoice == 2)
                    {
                        UnityEditor.PackageManager.UI.Window.Open("com.fastscriptreload");
                    }
                }

                return path;
            }, (screen) =>
            {
                GUILayout.Label(
                    $@"Asset is very simple to use:

1) Hit play to start.
2) Go to 'FunctionLibrary.cs' ({@"Assets/FastScriptReload/Examples/Scripts/"})", screen.TextStyle);
                
                CreateOpenFunctionLibraryOnRippleMethodButton();

                
                GUILayout.Label(
                    $@"3) Change 'Ripple' method (eg change line before return statement to 'p.z = v * 10'
4) Save file
5) See change immediately",
                    screen.TextStyle
                );
                
                GUILayout.Space(10);
                EditorGUILayout.HelpBox("There are some limitations to what can be Hot-Reloaded, documentation lists them under 'limitations' section.", MessageType.Warning);
            }), MainScrollViewSection);

        static void OnScriptHotReloadNoInstance() 
        { 
            Debug.Log("Reloaded - start");
            LastInspectFileHotReloadStateError = (DynamicFileHotReloadState) HarmonyLib.AccessTools
                .Field("FastScriptReload.Editor.FastScriptReloadWelcomeScreen:LastInspectFileHotReloadStateError")
                .GetValue(null);
            Debug.Log("Reloaded - end");
        }
        
        protected static List<GuiSection> CreateLeftSections(List<ChangeMainViewButton> additionalSections, LaunchSceneButton launchSceneButton, ScrollViewGuiSection mainScrollViewSection)
        {
            return new List<GuiSection>() {
                new GuiSection("", new List<ClickableElement>
                {
                    (InspectError = new ChangeMainViewButton("Error - Inspect", (screen) =>
                    {
            if (FastScriptReloadWelcomeScreen.LastInspectFileHotReloadStateError == null)
            {
                GUILayout.Label(
                    @"No error selected. Possibly it's been cleared by domain reload.

Choose other tab on the left.", screen.TextStyle);
                return;
            }


            EditorGUILayout.HelpBox(
                @"Errors are usually down to compilation / rewrite issue. There are ways you can mitigate those.",
                MessageType.Warning);
            GUILayout.Space(10);

            GUILayout.Label("1) Review compilation error, especially looking for specific lines that caused error:");
            EditorGUILayout.HelpBox(
                @"For example following error below shows line 940 as causing compilation issue due to missing #endif directive.

System.Exception: Compiler failed to produce the assembly. 
Output: '<filepath>.SourceCodeCombined.cs(940,1): error CS1027: #endif directive expected'",
                MessageType.Info);

            GUILayout.Space(10);
            GUILayout.Label("Error:");
            GUILayout.TextArea(LastInspectFileHotReloadStateError.ErrorText);

            GUILayout.Space(10);
            if (GUILayout.Button("2) Click here to open generated file that failed to compile"))
            {
                InternalEditorUtility.OpenFileAtLineExternal(LastInspectFileHotReloadStateError.SourceCodeCombinedFilePath, 1);
            }

            GUILayout.Label(
                @"Error could be caused by a normal compilation issue that you created in source file 
(eg typo), in that case please fix and it'll recompile.

It's possible compilation fails due to existing limitation, while I work continuously 
on mitigating limitations it's best that you're aware where they are.

Please see documentation (link above) to understand them better 
They also contain workarounds if needed.");

            GUILayout.Space(10);
            GUILayout.Label(
                @"You can also create one-off override file that'll allow to specify
custom rewrites for methods.", screen.BoldTextStyle);
            if (GUILayout.Button("3) Create User Defined Script Override"))
            {
                ScriptGenerationOverridesManager.AddScriptOverride(new FileInfo(FastScriptReloadWelcomeScreen.LastInspectFileHotReloadStateError.FullFileName));
            }

            GUILayout.Space(10);
            GUILayout.Label(@"You can help make FSR better!", screen.BoldTextStyle);
            EditorGUILayout.HelpBox(@"Could you please assist in improving the tool by providing me with the details of the error? 
I can use those to recreate the issue and fix the limitation.

Simply click the button below - it'll create a support pack automatically.

Support pack contains:
1) Original script file that caused error
2) Patched script file that was generated
3) Error message", MessageType.Warning);

            if (GUILayout.Button("4) Click here to create support-pack"))
            {
                try
                {
                    var folder = EditorUtility.OpenFolderPanel("Select Folder", "", "");
                    var sourceCodeCombinedFile = new FileInfo(LastInspectFileHotReloadStateError.SourceCodeCombinedFilePath);
                    var originalFile = new FileInfo(LastInspectFileHotReloadStateError.FullFileName);
                    File.Copy(LastInspectFileHotReloadStateError.SourceCodeCombinedFilePath, Path.Combine(folder, sourceCodeCombinedFile.Name));
                    File.Copy(LastInspectFileHotReloadStateError.FullFileName, Path.Combine(folder, originalFile.Name));
                    File.WriteAllText(Path.Combine(folder, "error-message.txt"), LastInspectFileHotReloadStateError.ErrorText);
                    
                    EditorUtility.DisplayDialog("Support Pack Created", $"Thanks!\r\n\r\nPlease send files from folder:\r\n'{folder}'\r\n\r\nto:\r\n\r\nsupport@immersivevrtools.com", "Ok, copy email to clipboard");
                    EditorGUIUtility.systemCopyBuffer = "support@immersivevrtools.com";
                }
                catch (Exception e)
                {
                    Debug.LogError($"Unable to create support pack., {e}");
                }
            }
                    })).WithShouldRender(() => LastInspectFileHotReloadStateError != null), 
                    new LastUpdateButton("New Update!", (screen) => LastUpdateUpdateScrollViewSection.RenderMainScrollViewSection(screen)),
                    new ChangeMainViewButton("Welcome", (screen) => mainScrollViewSection.RenderMainScrollViewSection(screen)),
                }),
                new GuiSection("Options", new List<ClickableElement>
                {
                    new ChangeMainViewButton("Settings", (screen) =>
                    {
                        GUILayout.Label("Settings are in Project Settings -> Fast Script Reload.", screen.TextStyle);
                        GUILayout.Space(10);
                        if (GUILayout.Button("Open Settings"))
                        {
                            FastScriptReloadSettingsProvider.Open();
                        }
                    }),
                    (UserScriptRewriteOverrides = new ChangeMainViewButton("User Script\r\nRewrite Overrides", (screen) =>
                    {
                        EditorGUILayout.HelpBox(
                            $@"For tool to work it'll need to slightly adjust your code to make it compilable. Sometimes due to existing limitations this can fail and you'll see an error.

You can specify custom script rewrite overrides, those are specified for specific parts of code that fail, eg method. 

It will help overcome limitations in the short run while I work on implementing proper solution."
                            , MessageType.Info);
                        
                        EditorGUILayout.HelpBox(
                            $@"To add:
1) right-click in project panel on the file that causes the issue. 
2) select Fast Script Reload -> Add / Open User Script Rewrite Override

It'll open override file with template already in. You can read top comments that describe how to use it."
                            , MessageType.Warning);

                        EditorGUILayout.LabelField("Existing User Defined Script Overrides:", screen.BoldTextStyle);
                        Action executeAfterIteration = null;
                        foreach (var scriptOverride in ScriptGenerationOverridesManager.UserDefinedScriptOverrides)
                        {
                            EditorGUILayout.BeginHorizontal();
                            
                            EditorGUILayout.LabelField(scriptOverride.File.Name);
                            if (GUILayout.Button("Open"))
                            {
                                InternalEditorUtility.OpenFileAtLineExternal(scriptOverride.File.FullName, 0);
                            }
                            
                            if (GUILayout.Button("Delete"))
                            {
                                executeAfterIteration = () =>
                                {
                                    if (EditorUtility.DisplayDialog("Are you sure", "This will permanently remove override file.", "Delete", "Keep File"))
                                    {
                                        ScriptGenerationOverridesManager.TryRemoveScriptOverride(scriptOverride);
                                    }
                                };
                            }
                            
                            EditorGUILayout.EndHorizontal();
                        }
                        executeAfterIteration?.Invoke();
                    }))
                }.Concat(additionalSections).ToList()),
                new GuiSection("Launch Demo", new List<ClickableElement>
                {
                    launchSceneButton
                })
            };
        }

        private static readonly string RedirectBaseUrl = "https://immersivevrtools.com/redirect/fast-script-reload"; 
        private static readonly GuiSection TopSection = CreateTopSectionButtons(RedirectBaseUrl);

        protected static GuiSection CreateTopSectionButtons(string redirectBaseUrl)
        {
            return new GuiSection("Support", new List<ClickableElement>
                {
                    new OpenUrlButton("Documentation", $"{redirectBaseUrl}/documentation"),
                    new OpenUrlButton("Discord", $"{redirectBaseUrl}/discord"),
                    new OpenUrlButton("Github", $"{redirectBaseUrl}/github"),
                    new OpenUrlButton("Donate", $"{redirectBaseUrl}/donate", "sv_icon_name3")
                }
            );
        }

        private static readonly GuiSection BottomSection = new GuiSection(
            "I want to make this tool better. And I need your help!",
            $"Please spread the word and star github repo. Alternatively if you're in a position to make a donation I'd hugely appreciate that. It allows me to spend more time on the tool instead of paid client projects.",
            new List<ClickableElement>
            {
                new OpenUrlButton(" Star on Github", $"{RedirectBaseUrl}/github"),
                new OpenUrlButton(" Donate", $"{RedirectBaseUrl}/donate"),
            }
        );

        public override string WindowTitle { get; } = _WindowTitle;
        public override Vector2 WindowSizePx { get; } = _WindowSizePx;

#if !LiveScriptReload_Enabled
        [MenuItem("Window/Fast Script Reload/Start Screen", false, 1999)]
#endif
        public static FastScriptReloadWelcomeScreen Init()
        {
            return OpenWindow<FastScriptReloadWelcomeScreen>(_WindowTitle, _WindowSizePx);
        }
    
#if !LiveScriptReload_Enabled
        [MenuItem("Window/Fast Script Reload/Force Reload", true, 1999)]
#endif
        public static bool ForceReloadValidate()
        {
            return EditorApplication.isPlaying && (bool)FastScriptReloadPreference.EnableOnDemandReload.GetEditorPersistedValueOrDefault();
        }
    
#if !LiveScriptReload_Enabled
        [MenuItem("Window/Fast Script Reload/Force Reload", false, 1999)]
#endif
        public static void ForceReload()
        {
            if (!(bool)FastScriptReloadPreference.EnableOnDemandReload.GetEditorPersistedValueOrDefault())
            {
                LoggerScoped.LogWarning("On demand hot reload is disabled, can't perform. You can enable it in Project Settings -> Fast Script Reload -> Advanced");
                return;
            }
            
            FastScriptReloadManager.Instance.TriggerReloadForChangedFiles();
        }

        public void OnEnable()
        {
            OnEnableCommon(ProjectIconName);
        }

        public void OnGUI()
        {
            RenderGUI(LeftSections, TopSection, BottomSection, MainScrollViewSection);
        }
        
        protected static void CreateOpenFunctionLibraryOnRippleMethodButton()
        {
            if (GUILayout.Button("Open 'FunctionLibrary.cs'"))
            {
                var codeComponent = AssetDatabase.LoadAssetAtPath<MonoScript>(AssetDatabase.GUIDToAssetPath(AssetDatabase.FindAssets($"t:Script FunctionLibrary")[0]));
                CodeEditorManager.GotoScript(codeComponent, "Ripple");
            }
        }
    }

    public class FastScriptReloadPreference : ProductPreferenceBase
    {
        public const string BuildSymbol_DetailedDebugLogging = "ImmersiveVrTools_DebugEnabled";
        
        public const string ProductName = "Fast Script Reload";
        
        //New key on purpose, values stored for the previous 'BatchScriptChangesAndReloadEveryNSeconds' setting were in seconds
        public static readonly IntProjectEditorPreferenceDefinition ReloadAfterChangesSettleForNMilliseconds = new IntProjectEditorPreferenceDefinition(
            "Reload after no further changes for N milliseconds", "ReloadAfterChangesSettleForNMilliseconds", 100);

        public static readonly ToggleProjectEditorPreferenceDefinition EnableAutoReloadForChangedFiles = new ToggleProjectEditorPreferenceDefinition(
            "Hot reload changed scripts in play mode", "EnableAutoReloadForChangedFiles", true);
        
        public static readonly ToggleProjectEditorPreferenceDefinition EnableOnDemandReload = new ToggleProjectEditorPreferenceDefinition(
            "Allow reloading on demand (Window -> Fast Script Reload -> Force Reload)", "EnableOnDemandReload", false);
        
        public static readonly StringListProjectEditorPreferenceDefinition FilesExcludedFromHotReload = new StringListProjectEditorPreferenceDefinition(
            "Files excluded from Hot-Reload", "FilesExcludedFromHotReload", new List<string> {}, isReadonly: true);
        
        public static readonly StringListProjectEditorPreferenceDefinition ReferencesExcludedFromHotReload = new StringListProjectEditorPreferenceDefinition(
            "References to exclude from Hot-Reload", "ReferencesExcludedFromHotReload", new List<string>
            {
                "ExCSS.Unity.dll"
            }, (newValue, oldValue) =>
            {
                DynamicCompilationBase.ReferencesExcludedFromHotReload = (List<string>)newValue;
            },
            (value) =>
            {
                DynamicCompilationBase.ReferencesExcludedFromHotReload = (List<string>)value;
            });
        
        public static readonly ToggleProjectEditorPreferenceDefinition IsAutoOpenGeneratedSourceFileOnChangeEnabled = new ToggleProjectEditorPreferenceDefinition(
            "Auto-open generated source file for debugging", "IsAutoOpenGeneratedSourceFileOnChangeEnabled", false);
        
        public static readonly ToggleProjectEditorPreferenceDefinition EnableDetailedDebugLogging = new ToggleProjectEditorPreferenceDefinition(
            "Enable detailed debug logging", "EnableDetailedDebugLogging", false,
            (object newValue, object oldValue) =>
            {
                BuildDefineSymbolManager.SetBuildDefineSymbolState(BuildSymbol_DetailedDebugLogging, (bool)newValue);
            },
            (value) =>
            {
                BuildDefineSymbolManager.SetBuildDefineSymbolState(BuildSymbol_DetailedDebugLogging, (bool)value);
            }
        );
        
        public static readonly ToggleProjectEditorPreferenceDefinition IsVisualHotReloadIndicationShownInProjectWindow = new ToggleProjectEditorPreferenceDefinition(
            "Show red / green bar in project window to indicate hot reload state for file", "IsVisualHotReloadIndicationShownInProjectWindow", true);
        
        public static readonly ToggleProjectEditorPreferenceDefinition EnableExperimentalEditorHotReloadSupport = new ToggleProjectEditorPreferenceDefinition(
            "Hot reload outside play mode (experimental)", "EnableExperimentalEditorHotReloadSupport", false);
        
        public static readonly EnumProjectEditorPreferenceDefinition FileWatcherImplementationInUse = new EnumProjectEditorPreferenceDefinition(
            "File Watcher implementation", "FileWatcherImplementationInUse", FileWatcherImplementation.UnityDefault, typeof(FileWatcherImplementation));


        public static void SetCommonMaterialsShader(ShadersMode newShaderModeValue)
        {
            var rootToolFolder = AssetPathResolver.GetAssetFolderPathRelativeToScript(ScriptableObject.CreateInstance(typeof(FastScriptReloadWelcomeScreen)), 1);
            if (rootToolFolder.Contains("/Scripts"))
            {
                rootToolFolder = rootToolFolder.Replace("/Scripts", ""); //if nested remove that and go higher level
            }
            var assets = AssetDatabase.FindAssets("t:Material Point", new[] { rootToolFolder });

            try
            {
                Shader shaderToUse = null;
                switch (newShaderModeValue)
                {
                    case ShadersMode.HDRP: shaderToUse = Shader.Find("Shader Graphs/Point URP"); break;
                    case ShadersMode.URP: shaderToUse = Shader.Find("Shader Graphs/Point URP"); break;
                    case ShadersMode.Surface: shaderToUse = Shader.Find("Graph/Point Surface"); break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }

                foreach (var guid in assets)
                {
                    var material = AssetDatabase.LoadAssetAtPath<Material>(AssetDatabase.GUIDToAssetPath(guid));
                    if (material.shader.name != shaderToUse.name)
                    {
                        material.shader = shaderToUse;
                    }
                }
            }
            catch (Exception ex)
            {
                LoggerScoped.LogWarning($"Shader does not exist: {ex.Message}");
            }
        }

        public static readonly ProjectEditorPreferenceDefinitionBase ShowStartScreenOnStartup = CreateDefaultShowOptionPreferenceDefinition();

        public enum ShadersMode
        {
            HDRP,
            URP,
            Surface
        }
    }

#if !LiveScriptReload_Enabled
    [InitializeOnLoad]
#endif
    public class FastScriptReloadWelcomeScreenInitializer : WelcomeScreenInitializerBase
    {
#if !LiveScriptReload_Enabled
        static FastScriptReloadWelcomeScreenInitializer()
        {
            var userId = ProductPreferenceBase.CreateDefaultUserIdDefinition(FastScriptReloadWelcomeScreen.ProjectName).GetEditorPersistedValueOrDefault().ToString();

            HandleUnityStartup(
                () => FastScriptReloadWelcomeScreen.Init(),
                FastScriptReloadWelcomeScreen.GenerateGetUpdatesUrl(userId, FastScriptReloadWelcomeScreen.VersionId),
                new List<ProjectEditorPreferenceDefinitionBase>(),
                (isFirstRun) => { }
            );
            
            InitCommon();
        }

#endif
        
        protected static void InitCommon()
        {
            DisplayMessageIfLastDetourPotentiallyCrashedEditor();

            //Rewrite reasons in the generated file help when debugging FSR itself
            DynamicCompilationBase.DebugWriteRewriteReasonAsComment = (bool)FastScriptReloadPreference.EnableDetailedDebugLogging.GetEditorPersistedValueOrDefault();
            DynamicCompilationBase.ReferencesExcludedFromHotReload = (List<string>)FastScriptReloadPreference.ReferencesExcludedFromHotReload.GetElements();
            FastScriptReloadManager.Instance.AssemblyChangesLoaderEditorOptionsNeededInBuild.UpdateValues(false, true);
            
            BuildDefineSymbolManager.SetBuildDefineSymbolState(FastScriptReloadPreference.BuildSymbol_DetailedDebugLogging,
                (bool)FastScriptReloadPreference.EnableDetailedDebugLogging.GetEditorPersistedValueOrDefault()
            );
            
            AutoDetectAndSetShaderMode();
        }

        private static void DisplayMessageIfLastDetourPotentiallyCrashedEditor()
        {
            const string firstInitSessionKey = "FastScriptReloadWelcomeScreenInitializer_FirstInitDone";
            if (!SessionState.GetBool(firstInitSessionKey, false))
            {
                SessionState.SetBool(firstInitSessionKey, true);

                var lastDetour = DetourCrashHandler.RetrieveLastDetour();
                if (!string.IsNullOrEmpty(lastDetour))
                {
                    EditorUtility.DisplayDialog("Fast Script Reload",
                        $@"That's embarrassing!

It seems like I've crashed your editor, sorry!

Last detoured method was: '{lastDetour}'

If this happens again, please reach out via support and we'll sort it out.

In the meantime, you can exclude any file from Hot-Reload by 
1) right-clicking on .cs file in Project menu
2) Fast Script Reload 
3) Add Hot-Reload Exclusion
", "Ok");
                    DetourCrashHandler.ClearDetourLog();
                }
            }
        }

        protected static void AutoDetectAndSetShaderMode()
        {
            var usedShaderMode = FastScriptReloadPreference.ShadersMode.Surface;
            
#if UNITY_6000_0_OR_NEWER
            var renderPipelineAsset = GraphicsSettings.defaultRenderPipeline;
#else
            var renderPipelineAsset = GraphicsSettings.renderPipelineAsset;
#endif

            if (renderPipelineAsset == null)
            {
                usedShaderMode = FastScriptReloadPreference.ShadersMode.Surface;
            }
            else if (renderPipelineAsset.GetType().Name.Contains("HDRenderPipelineAsset"))
            {
                usedShaderMode = FastScriptReloadPreference.ShadersMode.HDRP;
            }
            else if (renderPipelineAsset.GetType().Name.Contains("UniversalRenderPipelineAsset"))
            {
                usedShaderMode = FastScriptReloadPreference.ShadersMode.URP;
            }
        
            FastScriptReloadPreference.SetCommonMaterialsShader(usedShaderMode);
        }
    }
}