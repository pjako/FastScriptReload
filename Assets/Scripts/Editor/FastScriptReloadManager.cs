using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FastScriptReload.Editor.Compilation;
using FastScriptReload.Editor.Compilation.ScriptGenerationOverrides;
using FastScriptReload.Runtime;
using ImmersiveVRTools.Editor.Common.Utilities;
using ImmersiveVRTools.Runtime.Common;
using ImmersiveVrToolsCommon.Runtime.Logging;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace FastScriptReload.Editor
{
    [InitializeOnLoad]
    [PreventHotReload]
    public class FastScriptReloadManager
    {
        private static FastScriptReloadManager _instance;
        public static FastScriptReloadManager Instance
        {
            get {
                if (_instance == null)
                {
                    _instance = new FastScriptReloadManager();
                    LoggerScoped.LogDebug("Created Manager");
                }

                return _instance;
            }
        }

        private static string DataPath = Application.dataPath;
        

        private const int BaseMenuItemPriority_ManualScriptOverride = 100;
        private const int BaseMenuItemPriority_Exclusions = 200;
        
        private Dictionary<string, DynamicFileHotReloadState> _lastProcessedDynamicFileHotReloadStatesInSession = new Dictionary<string, DynamicFileHotReloadState>();
        public IReadOnlyDictionary<string, DynamicFileHotReloadState> LastProcessedDynamicFileHotReloadStatesInSession => _lastProcessedDynamicFileHotReloadStatesInSession;
        public event Action<List<DynamicFileHotReloadState>> HotReloadFailed;
        public event Action<List<DynamicFileHotReloadState>> HotReloadSucceeded;

        private bool _wasLockReloadAssembliesCalled;
        private PlayModeStateChange _lastPlayModeStateChange;
        private List<IDisposable> _fileWatchers = new List<IDisposable>();
        private IEnumerable<string> _currentFileExclusions;
        //Outside play mode, a full domain reload clears out the assemblies loaded by hot reload
        private const int TriggerDomainReloadAfterNHotReloadsOutsidePlayMode = 50;
#pragma warning disable 0618
        public AssemblyChangesLoaderEditorOptionsNeededInBuild AssemblyChangesLoaderEditorOptionsNeededInBuild { get; private set; } = new AssemblyChangesLoaderEditorOptionsNeededInBuild();

#pragma warning restore 0618

        //File watchers add entries from their own threads, every access needs to hold the lock
        private List<DynamicFileHotReloadState> _dynamicFileHotReloadStateEntries = new List<DynamicFileHotReloadState>();
        private DateTime _lastFileChangeOn;
        private readonly object _dynamicFileHotReloadStateEntriesLock = new object();

        //Compilation runs on a background thread, its results are applied from Update on the main thread
        private readonly ConcurrentQueue<Action> _mainThreadActions = new ConcurrentQueue<Action>();
        //Only one reload at a time, otherwise an older compilation finishing last could overwrite newer changes. Main thread only
        private bool _isHotReloadInProgress;
        //Read from file watcher threads, replaced when watchers are set up
        private volatile ProjectScripts _projectScripts;
        //Set from file watcher threads
        private volatile bool _isUnityRefreshRequested;
        private volatile bool _hadFileChangesInPlayMode;

        private bool _assemblyChangesLoaderResolverResolutionAlreadyCalled;
        private bool _isEditorModeHotReloadEnabled;
        private int _hotReloadPerformedCount = 0;
        private bool _isOnDemandHotReloadEnabled;

        private void OnWatchedFileChange(object source, FileSystemEventArgs e)
        {
            if (ShouldIgnoreFileChange())
            {
                RequestUnityRefresh();
                return;
            }

            var filePathToUse = e.FullPath;
            if (!File.Exists(filePathToUse))
            {
                if (!TryWorkaroundForUnityFileWatcherBug(e, ref filePathToUse)) 
                    return;
            }
            
            AddFileChangeToProcess(filePathToUse);
        }

        public void AddFileChangeToProcess(string filePath)
        {
            if (_lastPlayModeStateChange == PlayModeStateChange.EnteredPlayMode)
            {
                _hadFileChangesInPlayMode = true;
            }

            if (!File.Exists(filePath))
            {
                LoggerScoped.LogWarning($"Specified file: '{filePath}' does not exist. Hot-Reload will not be performed.");
                return;
            }

            var projectScripts = _projectScripts;
            if (projectScripts != null && !projectScripts.IsHotReloadable(filePath))
            {
                LoggerScoped.LogDebug($"File: '{filePath}' changed, but isn't compiled into a project assembly that can be hot reloaded, ignoring.");
                return;
            }

            if (_currentFileExclusions != null && _currentFileExclusions.Any(fp => filePath.Replace("\\", "/").EndsWith(fp)))
            {
                LoggerScoped.LogWarning($"FastScriptReload: File: '{filePath}' changed, but marked as exclusion. Hot-Reload will not be performed. You can manage exclusions via" +
                                        $"\r\nRight click context menu (Fast Script Reload > Add / Remove Hot-Reload exclusion)" +
                                        $"\r\nor via Project Settings -> Fast Script Reload");
            
                return;
            }
            
            lock (_dynamicFileHotReloadStateEntriesLock)
            {
                _lastFileChangeOn = DateTime.UtcNow;

                //The file is read when compilation starts, so a change still waiting to be compiled already covers this one.
                //Expected when one save raises several events (eg Renamed and Changed), or when file watchers overlap
                var isAlreadyAwaitingCompilation = _dynamicFileHotReloadStateEntries
                    .Any(f => f.FullFileName == filePath && f.IsAwaitingCompilation);
                if (isAlreadyAwaitingCompilation)
                {
                    LoggerScoped.LogDebug($"FastScriptReload: Looks like change to: {filePath} have already been added for processing.");
                    return;
                }

                _dynamicFileHotReloadStateEntries.Add(new DynamicFileHotReloadState(filePath, DateTime.UtcNow));
            }
        }

        public bool ShouldIgnoreFileChange()
        {
            if (!_isEditorModeHotReloadEnabled && _lastPlayModeStateChange != PlayModeStateChange.EnteredPlayMode)
            {
#if ImmersiveVrTools_DebugEnabled
                LoggerScoped.Log("Application not playing, file changes won't be compiled and hot reloaded");
#endif
                return true;
            }

            return false;
        }

        private void StartWatchingDirectoryAndSubdirectories(string directoryPath, string filter, bool includeSubdirectories) 
        {
            var directoryInfo = new DirectoryInfo(directoryPath);
            if (!directoryInfo.Exists)
            {
                LoggerScoped.LogWarning($"FastScriptReload: Directory: '{directoryPath}' does not exist, changes to scripts in it won't be hot reloaded.");
            }

            switch ((FileWatcherImplementation)FastScriptReloadPreference.FileWatcherImplementationInUse.GetEditorPersistedValueOrDefault())
            {
                case FileWatcherImplementation.UnityDefault:
                    var fileWatcher = new FileSystemWatcher();

                    fileWatcher.Path = directoryInfo.FullName;
                    fileWatcher.IncludeSubdirectories = includeSubdirectories;
                    fileWatcher.Filter =  filter;
                    fileWatcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName;
                    fileWatcher.Changed += OnWatchedFileChange;

                    // Editors with 'safe write' (eg Rider) save to a temporary file and rename it over the original,
                    // which raises Renamed instead of Changed
                    fileWatcher.Renamed += (source, e) =>
                    {
                        if (e.Name.EndsWith(".cs"))
                        {
                            OnWatchedFileChange(source, e);
                        }
                    };

                    fileWatcher.EnableRaisingEvents = true;
        
                    _fileWatchers.Add(fileWatcher);
                    
                    break;
#if UNITY_2021_1_OR_NEWER && UNITY_EDITOR_WIN
                case FileWatcherImplementation.DirectWindowsApi: 
                // On Windows, this is a WindowsFileSystemWatcher.
                // On other platforms, it's the default Mono implementation.
                // The WindowsFileSystemWatcher has much lower latency on Windows.
                // However, there's a small issue:
                // The WindowsFileSystemWatcher can, theoretically, miss events.
                // This is true in Microsoft's implementation as well as ours.
                // (Actually, ours should be slightly better.)
                // This can happen if a change occurs during the brief moment
                // during which the previous batch of changes are being
                // recorded and queued.
                // It can also happen if too many changes occur at once, overwhelming
                // the buffer.
                // People seem to routinely use the basic MS filewatcher and ignore
                // these issues, treating them as acceptably unlikely.
                // Our current implementation here does that too, but we may want
                // to look at eliminating this issue.
                // Unfortunately, it's a limitation of the Windows API, and to
                // my knowledge can't be avoided directly.
                // The solution is to combine the file watcher with a polling
                // mechanism which can (slowly, but reliably) catch any missed events.
                var windowsFileSystemWatcher = new WindowsFileSystemWatcher()
                {
                    Path = directoryInfo.FullName,
                    IncludeSubdirectories = includeSubdirectories,
                    Filter = filter,
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName
                };
                windowsFileSystemWatcher.Changed += OnWatchedFileChange;

                // Visual Studio is annoying.
                // It doesn't actually trigger a nice Changed event.
                // Instead, it goes through some elaborate procedure.
                // When changing player.cs, it does:
                // CREATE: Code\ua4tt1aw.4ae~
                // CHANGE: Code\ua4tt1aw.4ae~
                // CREATE: Code\Player.cs~RF70560f7.TMP
                // REMOVE: Code\Player.cs~RF70560f7.TMP
                // RENAME: Code\Player.cs -> Code\Player.cs~RF70560f7.TMP
                // RENAME: Code\ua4tt1aw.4ae~ -> Code\Player.cs
                // REMOVE: Code\Player.cs~RF70560f7.TMP - again, somehow?
                //
                // This was fine before, because the watcher implementation was polling.
                // I guess this usually happens fast between polls, so it just looks like a change.
                // Note that that may mean there's a potential bug if polling happens in the middle of this procedure.
                //
                // This fix is a temporary measure.
                // We should probably think more seriously about maybe being able to catch file additions and renames.
                // If we dealt with those extra events smoothly, this would probably just work.
                //
                // Other IDEs may do similar but different things. We want a general purpose solution, not a VS specific one.
                // Perhaps the approach should be to keep track of touched files, then do some diff procedure to work out what's happened to them?
                // This could, perhaps, be integrated into the file watcher robustness polling solution discussed above.
                // The two systems share a need for some type of event debouncing.
                windowsFileSystemWatcher.Renamed += (source, e) =>
                {
                    if (e.Name.EndsWith(".cs"))
                        OnWatchedFileChange(source, e);
                };
        
                windowsFileSystemWatcher.EnableRaisingEvents = true;
                                    
                _fileWatchers.Add(windowsFileSystemWatcher);
                break;
#endif
                
                case FileWatcherImplementation.CustomPolling:
                    CustomFileWatcher.InitializeSingularFilewatcher(directoryPath, filter, includeSubdirectories);
                    break;
                
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        static FastScriptReloadManager()
        {
            //do not add init code in here as with domain reload turned off it won't be properly set on play-mode enter, use Init method instead
            EditorApplication.update += Instance.Update;
            EditorApplication.playModeStateChanged += Instance.OnEditorApplicationOnplayModeStateChanged;
        }

        ~FastScriptReloadManager()
        {
            LoggerScoped.LogDebug("Destroying FSR Manager "); 
            if (_instance != null)
            {
                if (_lastPlayModeStateChange == PlayModeStateChange.EnteredPlayMode)
                {
                    LoggerScoped.LogError("Manager is being destroyed in play session, this indicates some sort of issue where static variables were reset, hot reload will not function properly please reset. " +
                                          "This is usually caused by Unity triggering that reset for some reason that's outside of asset control - other static variables will also be affected and recovering just hot reload would hide wider issue.");
                }
                ClearFileWatchers();
            }
        }

        [MenuItem("Assets/Fast Script Reload/Add \\ Open User Script Rewrite Override", false, BaseMenuItemPriority_ManualScriptOverride + 1)]
        public static void AddHotReloadManualScriptOverride()
        {
            if (Selection.activeObject is MonoScript script)
            {
                ScriptGenerationOverridesManager.AddScriptOverride(script);
            }
        }
        
        [MenuItem("Assets/Fast Script Reload/Add \\ Open User Script Rewrite Override", true)]
        public static bool AddHotReloadManualScriptOverrideValidateFn()
        {
            return Selection.activeObject is MonoScript;
        }
        
        [MenuItem("Assets/Fast Script Reload/Remove User Script Rewrite Override", false, BaseMenuItemPriority_ManualScriptOverride + 2)]
        public static void RemoveHotReloadManualScriptOverride()
        {
            if (Selection.activeObject is MonoScript script)
            {
                ScriptGenerationOverridesManager.TryRemoveScriptOverride(script);
            }
        }
        
        [MenuItem("Assets/Fast Script Reload/Remove User Script Rewrite Override", true)]
        public static bool RemoveHotReloadManualScriptOverrideValidateFn()
        {
            if (Selection.activeObject is MonoScript script)
            {
                return ScriptGenerationOverridesManager.TryGetScriptOverride(  
                    new FileInfo(Path.Combine(Path.Combine(Application.dataPath + "//..", AssetDatabase.GetAssetPath(script)))),
                    out var _
                );
            }

            return false;
        }
        
        [MenuItem("Assets/Fast Script Reload/Show User Script Rewrite Overrides", false, BaseMenuItemPriority_ManualScriptOverride + 3)]
        public static void ShowManualScriptRewriteOverridesInUi()
        {
            var window = FastScriptReloadWelcomeScreen.Init();
            window.OpenUserScriptRewriteOverridesSection();
        }
        
        [MenuItem("Assets/Fast Script Reload/Add Hot-Reload Exclusion", false, BaseMenuItemPriority_Exclusions + 1)]
        public static void AddFileAsExcluded()
        {
            FastScriptReloadPreference.FilesExcludedFromHotReload.AddElement(ResolveRelativeToAssetDirectoryFilePath(Selection.activeObject));
        }

        [MenuItem("Assets/Fast Script Reload/Add Hot-Reload Exclusion", true)]
        public static bool AddFileAsExcludedValidateFn()
        {
            return Selection.activeObject is MonoScript
                   && !((FastScriptReloadPreference.FilesExcludedFromHotReload.GetEditorPersistedValueOrDefault() as IEnumerable<string>) ?? Array.Empty<string>())
                       .Contains(ResolveRelativeToAssetDirectoryFilePath(Selection.activeObject));
        }

        [MenuItem("Assets/Fast Script Reload/Remove Hot-Reload Exclusion", false, BaseMenuItemPriority_Exclusions + 2)]
        public static void RemoveFileAsExcluded()
        {
            FastScriptReloadPreference.FilesExcludedFromHotReload.RemoveElement(ResolveRelativeToAssetDirectoryFilePath(Selection.activeObject));
        }
    
        [MenuItem("Assets/Fast Script Reload/Remove Hot-Reload Exclusion", true)]
        public static bool RemoveFileAsExcludedValidateFn()
        {
            return Selection.activeObject is MonoScript
                   && ((FastScriptReloadPreference.FilesExcludedFromHotReload.GetEditorPersistedValueOrDefault() as IEnumerable<string>) ?? Array.Empty<string>())
                   .Contains(ResolveRelativeToAssetDirectoryFilePath(Selection.activeObject));
        }
    
        [MenuItem("Assets/Fast Script Reload/Show Exclusions", false, BaseMenuItemPriority_Exclusions + 3)]
        public static void ShowExcludedFilesInUi()
        {
            FastScriptReloadSettingsProvider.Open();
        }
        
        private static string ResolveRelativeToAssetDirectoryFilePath(UnityEngine.Object obj)
        {
            //The Object overload exists in all versions, the instance id one is obsolete from 6000.3 (EntityId replaces it)
            return AssetDatabase.GetAssetPath(obj);
        }

        public void Update()
        {
            while (_mainThreadActions.TryDequeue(out var mainThreadAction))
            {
                mainThreadAction();
            }

            _isEditorModeHotReloadEnabled = (bool)FastScriptReloadPreference.EnableExperimentalEditorHotReloadSupport.GetEditorPersistedValueOrDefault();

            //File watchers also run outside play mode, changes there are compiled by Unity, see RequestUnityRefresh
            EnsureInitialized();
            if (_isUnityRefreshRequested)
            {
                RefreshUnityOnceChangesSettled();
            }

            if (!_isEditorModeHotReloadEnabled && !EditorApplication.isPlaying)
            {
                return;
            }

            //Only runs once per domain reload, here as this is when hot reload becomes active
            DynamicAssemblyCompiler.WarmUpInBackground();

            AssignConfigValuesThatCanNotBeAccessedOutsideOfMainThread();

            if (!_assemblyChangesLoaderResolverResolutionAlreadyCalled)
            {
                AssemblyChangesLoaderResolver.Instance.Resolve(); //WARN: need to resolve initially in case monobehaviour singleton is not created
                _assemblyChangesLoaderResolverResolutionAlreadyCalled = true;
            }

            if ((bool)FastScriptReloadPreference.EnableAutoReloadForChangedFiles.GetEditorPersistedValueOrDefault()
                && !_isHotReloadInProgress
                && HaveFileChangesSettled((int)FastScriptReloadPreference.ReloadAfterChangesSettleForNMilliseconds.GetEditorPersistedValueOrDefault()))
            {
                TriggerReloadForChangedFiles();
            }
        }

        //Waits for a short quiet period, so changes saved together (eg 'save all', a refactoring) are compiled in one go
        private bool HaveFileChangesSettled(int settleForMilliseconds)
        {
            lock (_dynamicFileHotReloadStateEntriesLock)
            {
                return (DateTime.UtcNow - _lastFileChangeOn).TotalMilliseconds >= settleForMilliseconds
                       && _dynamicFileHotReloadStateEntries.Any(e => e.IsAwaitingCompilation);
            }
        }
        
        private static void ClearFileWatchers()
        {
            foreach (var fileWatcher in Instance._fileWatchers)
            {
                fileWatcher.Dispose();
            }

            Instance._fileWatchers.Clear();
        }

        private void AssignConfigValuesThatCanNotBeAccessedOutsideOfMainThread()
        {
            //TODO: PERF: needed in file watcher but when run on non-main thread causes exception. 
            _currentFileExclusions = FastScriptReloadPreference.FilesExcludedFromHotReload.GetElements();
            _isOnDemandHotReloadEnabled = (bool)FastScriptReloadPreference.EnableOnDemandReload.GetEditorPersistedValueOrDefault();
            //Added fields are always supported, the fields check only applies without that support
            AssemblyChangesLoaderEditorOptionsNeededInBuild.UpdateValues(false, true);
        }

        public void TriggerReloadForChangedFiles()
        {
            if (!Application.isPlaying && _hotReloadPerformedCount > TriggerDomainReloadAfterNHotReloadsOutsidePlayMode)
            {
                _hotReloadPerformedCount = 0;
                LoggerScoped.LogWarning($"Dynamically created assembles reached over: {TriggerDomainReloadAfterNHotReloadsOutsidePlayMode} - triggering full domain reload to clean up.");
#if UNITY_2019_3_OR_NEWER
                CompilationPipeline.RequestScriptCompilation(); //TODO: add some timer to ensure this does not go into some kind of loop
#elif UNITY_2017_1_OR_NEWER
                 var editorAssembly = Assembly.GetAssembly(typeof(Editor));
                 var editorCompilationInterfaceType = editorAssembly.GetType("UnityEditor.Scripting.ScriptCompilation.EditorCompilationInterface");
                 var dirtyAllScriptsMethod = editorCompilationInterfaceType.GetMethod("DirtyAllScripts", BindingFlags.Static | BindingFlags.Public);
                 dirtyAllScriptsMethod.Invoke(editorCompilationInterfaceType, null);
#endif
                ClearLastProcessedDynamicFileHotReloadStates();
            }
            
            if (_isHotReloadInProgress)
            {
                LoggerScoped.Log("FastScriptReload: Hot reload already in progress, remaining changes will be picked up by the next reload.");
                return;
            }

            var assemblyChangesLoader = AssemblyChangesLoaderResolver.Instance.Resolve();
            List<DynamicFileHotReloadState> changesAwaitingHotReload;
            lock (_dynamicFileHotReloadStateEntriesLock)
            {
                //Status shown per file is kept in _lastProcessedDynamicFileHotReloadStatesInSession, finished entries aren't needed here
                _dynamicFileHotReloadStateEntries.RemoveAll(e => e.IsChangeHotSwapped || e.IsFailed);

                changesAwaitingHotReload = _dynamicFileHotReloadStateEntries
                    .Where(e => e.IsAwaitingCompilation)
                    .ToList();
                foreach (var c in changesAwaitingHotReload)
                {
                    c.IsBeingProcessed = true;
                }
            }

            if (changesAwaitingHotReload.Any())
            {
                UpdateLastProcessedDynamicFileHotReloadStates(changesAwaitingHotReload);

                //Cleared on the main thread once the result is applied or has failed
                _isHotReloadInProgress = true;
                var unityMainThreadDispatcher = UnityMainThreadDispatcher.Instance.EnsureInitialized(); //need to pass that in, resolving on other than main thread will cause exception
                Task.Run(() =>
                {
                    List<string> sourceCodeFilesWithUniqueChangesAwaitingHotReload = null;
                    try
                    {
                        sourceCodeFilesWithUniqueChangesAwaitingHotReload = changesAwaitingHotReload
                            .GroupBy(e => e.FullFileName)
                            .Select(e => e.First().FullFileName).ToList();
                    
                        var dynamicallyLoadedAssemblyCompilerResult = DynamicAssemblyCompiler.Compile(sourceCodeFilesWithUniqueChangesAwaitingHotReload, unityMainThreadDispatcher);
                        if (!dynamicallyLoadedAssemblyCompilerResult.IsError)
                        {
                            //Builds the type lookup here so the main thread doesn't stall on it during the first hot reload
                            _ = ProjectTypeCache.AllTypesInNonDynamicGeneratedAssemblies;

                            //Methods are swapped on the main thread, between frames, so it can't be executing a method while its code is being overwritten
                            _mainThreadActions.Enqueue(() => ApplyCompiledChanges(dynamicallyLoadedAssemblyCompilerResult, assemblyChangesLoader,
                                changesAwaitingHotReload, sourceCodeFilesWithUniqueChangesAwaitingHotReload));
                        }
                        else
                        {
                            //Always reported as failure, otherwise the reload would never finish
                            var msg = new StringBuilder();
                            foreach (string message in dynamicallyLoadedAssemblyCompilerResult.MessagesFromCompilerProcess)
                            {
                                msg.AppendLine($"Error  when compiling, it's best to check code and make sure it's compilable \r\n {message}\n");
                            }

                            throw new Exception(msg.Length > 0 ? msg.ToString() : "Compilation failed without compiler messages");
                        }
                    }
                    catch (Exception ex)
                    {
                        _mainThreadActions.Enqueue(() => HandleHotReloadFailure(ex, changesAwaitingHotReload, sourceCodeFilesWithUniqueChangesAwaitingHotReload));
                    }
                });
            }
        }

        private void ApplyCompiledChanges(CompileResult compileResult, IAssemblyChangesLoader assemblyChangesLoader,
            List<DynamicFileHotReloadState> changesAwaitingHotReload, List<string> sourceCodeFiles)
        {
            try
            {
                changesAwaitingHotReload.ForEach(c =>
                {
                    c.FileCompiledOn = DateTime.UtcNow;
                    c.AssemblyNameCompiledIn = compileResult.CompiledAssemblyPath;
                });

                //TODO: return some proper results to make sure entries are correctly updated
                assemblyChangesLoader.DynamicallyUpdateMethodsForCreatedAssembly(compileResult.CompiledAssembly, AssemblyChangesLoaderEditorOptionsNeededInBuild);
                changesAwaitingHotReload.ForEach(c =>
                {
                    c.HotSwappedOn = DateTime.UtcNow;
                    c.IsBeingProcessed = false;
                }); //TODO: technically not all were hot swapped at same time

                _hotReloadPerformedCount++;

                SafeInvoke(HotReloadSucceeded, changesAwaitingHotReload);
            }
            catch (Exception ex)
            {
                HandleHotReloadFailure(ex, changesAwaitingHotReload, sourceCodeFiles);
            }
            finally
            {
                _isHotReloadInProgress = false;
            }
        }

        private void HandleHotReloadFailure(Exception ex, List<DynamicFileHotReloadState> changesAwaitingHotReload, List<string> sourceCodeFiles)
        {
            _isHotReloadInProgress = false;

            if (ex is SourceCodeHasErrorsException e)
            {
                LoggerScoped.LogError(e.Message + Environment.NewLine);
            }
            else
            {
                LoggerScoped.LogError($"Error when updating files: '{(sourceCodeFiles != null ? string.Join(",", sourceCodeFiles.Select(fn => new FileInfo(fn).Name)) : "unknown")}', {ex}");
            }

            changesAwaitingHotReload.ForEach(c =>
            {
                c.ErrorOn = DateTime.UtcNow;
                c.ErrorText = ex.Message;
                c.SourceCodeCombinedFilePath = (ex as HotReloadCompilationException)?.SourceCodeCombinedFileCreated;
            });

            SafeInvoke(HotReloadFailed, changesAwaitingHotReload);
        }

        private void SafeInvoke(Action<List<DynamicFileHotReloadState>> ev, List<DynamicFileHotReloadState> changesAwaitingHotReload)
        {
            try
            {
                ev?.Invoke(changesAwaitingHotReload);
            }
            catch (Exception e)
            {
                Debug.LogError($"Error when executing event, {e}");
            }
        }

        private void AddToLastProcessedDynamicFileHotReloadStates(DynamicFileHotReloadState c)
        {
            var assetGuid = AssetDatabaseHelper.AbsolutePathToGUID(c.FullFileName);
            if (!string.IsNullOrEmpty(assetGuid))
            {
                _lastProcessedDynamicFileHotReloadStatesInSession[assetGuid] = c;
            }
        }
        
        private void ClearLastProcessedDynamicFileHotReloadStates()
        {
            _lastProcessedDynamicFileHotReloadStatesInSession.Clear();
        }
        
        //Success entries will always be cleared - errors will remain till another change fixes them
        private void UpdateLastProcessedDynamicFileHotReloadStates(List<DynamicFileHotReloadState> changesToHotReload)
        {
            var succeededReloads = _lastProcessedDynamicFileHotReloadStatesInSession
                .Where(s => s.Value.IsChangeHotSwapped).ToList();
            foreach (var kv in succeededReloads)
            {
                _lastProcessedDynamicFileHotReloadStatesInSession.Remove(kv.Key);
            }

            foreach (var changeToHotReload in changesToHotReload)
            {
                AddToLastProcessedDynamicFileHotReloadStates(changeToHotReload);
            }
        }

        private void OnEditorApplicationOnplayModeStateChanged(PlayModeStateChange obj)
        {
            Instance._lastPlayModeStateChange = obj;

            //Unity may compile changed scripts while playing (Auto Refresh, 'Recompile And Continue Playing'), the domain reload
            //that follows would end the play session. Hot reload applies the changes instead, Unity's reload happens after playing
            if (obj == PlayModeStateChange.EnteredPlayMode && IsHotReloadEnabled())
            {
                _hadFileChangesInPlayMode = false;
                EditorApplication.LockReloadAssemblies();
                _wasLockReloadAssembliesCalled = true;
            }

            if(obj == PlayModeStateChange.EnteredEditMode && _wasLockReloadAssembliesCalled)
            {
                EditorApplication.UnlockReloadAssemblies();
                _wasLockReloadAssembliesCalled = false;
            }

            //Changes applied by hot reload still have to be compiled by Unity
            if (obj == PlayModeStateChange.EnteredEditMode && _hadFileChangesInPlayMode)
            {
                _hadFileChangesInPlayMode = false;
                _isUnityRefreshRequested = true;
            }
        }

        private static bool IsHotReloadEnabled()
        {
            return (bool)FastScriptReloadPreference.EnableAutoReloadForChangedFiles.GetEditorPersistedValueOrDefault()
                   || (bool)FastScriptReloadPreference.EnableOnDemandReload.GetEditorPersistedValueOrDefault();
        }

        /// <summary>
        /// Unity only looks for changed scripts when its window gets focus (Auto Refresh), changes saved while it's already focused
        /// or in the background would wait for a manual refresh. File watchers see them right away.
        /// </summary>
        public void RequestUnityRefresh()
        {
            lock (_dynamicFileHotReloadStateEntriesLock)
            {
                _lastFileChangeOn = DateTime.UtcNow;
            }
            _isUnityRefreshRequested = true;
        }

        private void RefreshUnityOnceChangesSettled()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                return;
            }

            var settleForMilliseconds = (int)FastScriptReloadPreference.ReloadAfterChangesSettleForNMilliseconds.GetEditorPersistedValueOrDefault();
            lock (_dynamicFileHotReloadStateEntriesLock)
            {
                if ((DateTime.UtcNow - _lastFileChangeOn).TotalMilliseconds < settleForMilliseconds)
                {
                    return;
                }
            }

            _isUnityRefreshRequested = false;
            AssetDatabase.Refresh();
        }
        
                private static bool TryWorkaroundForUnityFileWatcherBug(FileSystemEventArgs e, ref string filePathToUse)
        {
            LoggerScoped.LogWarning(@"Fast Script Reload - Unity File Path Bug - Warning!
Path for changed file passed by Unity does not exist. This is a known editor bug, more info: https://issuetracker.unity3d.com/issues/filesystemwatcher-returns-bad-file-path
                    
Best course of action is to update editor as issue is already fixed in newer (minor and major) versions.
                    
As a workaround asset will try to resolve paths via directory search.
                    
Workaround will search in all folders (under project root) and will use first found file. This means it's possible it'll pick up wrong file as there's no directory information available.");

            var changedFileName = new FileInfo(filePathToUse).Name;
            //TODO: try to look in all file watcher configured paths, some users might have code outside of assets, eg packages

            var fileFoundInAssets = Directory.GetFiles(DataPath, changedFileName, SearchOption.AllDirectories);
            if (fileFoundInAssets.Length == 0)
            {
                LoggerScoped.LogError($"FileWatcherBugWorkaround: Unable to find file '{changedFileName}', changes will not be reloaded. Please update unity editor.");
                return false;
            }
            else if (fileFoundInAssets.Length == 1)
            {
                LoggerScoped.Log($"FileWatcherBugWorkaround: Original Unity passed file path: '{e.FullPath}' adjusted to found: '{fileFoundInAssets[0]}'");
                filePathToUse = fileFoundInAssets[0];
                return true;
            }
            else
            {
                LoggerScoped.LogWarning($"FileWatcherBugWorkaround: Multiple files found. Original Unity passed file path: '{e.FullPath}' adjusted to found: '{fileFoundInAssets[0]}'");
                filePathToUse = fileFoundInAssets[0];
                return true;
            }
        }

        private static bool HotReloadDisabled_WarningMessageShownAlready;
        private static void EnsureInitialized()
        {
            if (!IsHotReloadEnabled())
            {
                if (!HotReloadDisabled_WarningMessageShownAlready)
                {
                    LoggerScoped.LogWarning($"Neither auto hot reload nor on-demand reload is enabled, file watchers will not be initialized. Please adjust settings and restart if you want hot reload to work.");
                    HotReloadDisabled_WarningMessageShownAlready = true;
                }
                return;
            }

            var isUsingCustomFileWatchers = (FileWatcherImplementation)FastScriptReloadPreference.FileWatcherImplementationInUse.GetEditorPersistedValueOrDefault() 
                                            == FileWatcherImplementation.CustomPolling;
            if (!isUsingCustomFileWatchers)
            {
                if (Instance._fileWatchers.Count == 0)
                {
                    WatchProjectScripts();
                }
            }
            else if(!CustomFileWatcher.InitSignaled)
            {
                CustomFileWatcher.TryEnableLivewatching();
                WatchProjectScripts();
                CustomFileWatcher.InitSignaled = true;
            }
        }

        //Watches the folders with the project's scripts, changes are then checked against the scripts Unity compiles
        private static void WatchProjectScripts()
        {
            Instance._projectScripts = ProjectScripts.Discover();
            foreach (var rootDirectory in Instance._projectScripts.RootDirectories)
            {
                Instance.StartWatchingDirectoryAndSubdirectories(rootDirectory, "*.cs", true);
            }

            RefreshUnityIfScriptsChangedDuringCompilation();
        }

        private const string LastCompilationStartedSessionKey = "FSR:LastCompilationStartedUtcTicks";
        //Unity may have only refreshed shortly before compilation started
        private static readonly TimeSpan CompilationStartMargin = TimeSpan.FromSeconds(2);

        [InitializeOnLoadMethod]
        private static void TrackCompilationStart()
        {
            CompilationPipeline.compilationStarted += _ => SessionState.SetString(LastCompilationStartedSessionKey, DateTime.UtcNow.Ticks.ToString());
        }

        //Scripts saved while Unity compiled and reloaded aren't seen by file watchers, there are none during the domain reload
        private static void RefreshUnityIfScriptsChangedDuringCompilation()
        {
            var lastCompilationStarted = SessionState.GetString(LastCompilationStartedSessionKey, string.Empty);
            SessionState.EraseString(LastCompilationStartedSessionKey);
            if (EditorApplication.isPlayingOrWillChangePlaymode || !long.TryParse(lastCompilationStarted, out var ticks))
            {
                return;
            }

            if (Instance._projectScripts.IsAnyScriptChangedSince(new DateTime(ticks, DateTimeKind.Utc) - CompilationStartMargin))
            {
                LoggerScoped.LogDebug("Scripts changed while Unity was compiling, refreshing again.");
                Instance.RequestUnityRefresh();
            }
        }
    }

    public class DynamicFileHotReloadState
    {
        public string FullFileName { get; set; }
        public DateTime FileChangedOn { get; set; }
        public bool IsAwaitingCompilation => !IsFileCompiled && !ErrorOn.HasValue && !IsBeingProcessed;
        public bool IsFileCompiled => FileCompiledOn.HasValue;
        public DateTime? FileCompiledOn { get; set; }
    
        public string AssemblyNameCompiledIn { get; set; }

        public bool IsAwaitingHotSwap => IsFileCompiled && !HotSwappedOn.HasValue;
        public DateTime? HotSwappedOn { get; set; }
        public bool IsChangeHotSwapped => HotSwappedOn.HasValue;
    
        public string ErrorText { get; set; }
        public DateTime? ErrorOn { get; set; }
        public bool IsFailed => ErrorOn.HasValue;
        public bool IsBeingProcessed { get; set; }
        public string SourceCodeCombinedFilePath { get; set; }

        public DynamicFileHotReloadState(string fullFileName, DateTime fileChangedOn)
        {
            FullFileName = fullFileName;
            FileChangedOn = fileChangedOn;
        }
    }

    public enum FileWatcherImplementation
    {
        UnityDefault = 0,
#if UNITY_EDITOR_WIN && UNITY_2021_1_OR_NEWER
        DirectWindowsApi = 1,
#endif
        CustomPolling = 2
    }
}
