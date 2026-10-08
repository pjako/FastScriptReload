2.0
- Apple Silicon Mac support, including OnScriptHotReload added in play mode and a 'Window -> Fast Script Reload -> Added Fields' window for added fields
- Generic methods and methods of generic classes are hot reloaded (see limitations for exceptions)
- Much faster reloads: compiled in-process with Roslyn and warmed up in the background, reloads start as soon as saved changes settle (new 'Reload after no further changes for N milliseconds' setting, replaces 'Batch script changes and reload every N seconds')
- Works with Unity's compilation: saving a script outside play mode starts Unity's compilation right away, scripts changed while playing are compiled after play mode, Unity's domain reload is held back while playing. Auto Refresh no longer needs changing
- Scripts to hot reload are found from Unity's compilation (Assets and embedded / local packages), manual file watcher setup removed
- Settings moved to 'Project Settings -> Fast Script Reload' and simplified, added fields, partial classes and the 'this' call fix are always enabled
- Changed code can use internal and private protected members of its assembly
- Lambdas, closures and running coroutines are no longer redirected to code that doesn't match them
- Fixed changes being missed or applied in the wrong order with several saves in quick succession
- Fixed crashes and errors from file watcher threads, the polling file watcher stopping, temporary files piling up, added static fields failing to compile
- Editors saving via temporary file and rename (eg Rider 'safe write') are picked up

1.9
- Added namespace support for partial classes (contributed by RunninglVlan)
- Updated Harmonylib version to support Mac Silicon (contributed by jagheterfredrik)

1.8
- Added Unity 6 support
- Improved extension methods rewriting
- Partial classes support (contributed by Jlabarca)
- Unity 2019 / 2020 no longer officially supported

1.7
- Optional file watching can be done via direct WindowsAPI (contributed by SamPruden)
- Extended UI to allow for easy choice of file watcher implementation

1.6
- Added more Unit Tests to ensure various code patterns can be rewritten correctly
- Added Watch Only Specific Files and Folders mode (contributed by GhatSmith)
- Added better Odin support for dynamically added fields (contributed by GhatSmith)
- Updated Roslyn lib version to 4.6.0 for better code parsing (contributed by SamPruden)
- Improved error handling for code parsing (contributed by SamPruden)
- Added Custom-File-Watcher implementation (for cases where standard FileWatcher API is causing issues) (contributed by D0rkKnight)

1.5
- you can now import FSR as a package from github page
- hot-reloading internal interfaces / classes would no longer cause compilation error
- OnScriptHotReload method can be added at runtime
- builder functions will be correctly rewritten
- hot reload status (red / green) will be visible in project panel next to changed script
- compilation error reporting will be much improved, project panel will allow to click error next to actual file for more details and workarounds
- all type declarations that were appended with __Patched_ postfix to avoid name collisions will also have all corresponding identifiers changed
- OnScriptHotReload methods will also be called in editor-workflow

1.4
- added experimental editor-mode hot-reload, you'll need to opt in via Start Screen -> Editor Hot-Reload (please read the notes on that page, playmode workflow is still far superior)
- you can now define User Script Rewrite Overrides - which will allow you to overcome most of existing limitations that cause compilation errors (on a one by one basis)
- big optimization in how long hot-reload part will take, first call will be unaffected when type cache is build but subsequent calls will be significantly quicker
- added 'Exclude References' options - this allows to remove specific dll references from dynamically compiled code (as in some cases you may get 'type defined in both assembly x.dll and y.dll'
- destructors will no longer cause compilation error
- (options opt-in) script rewriting can optionally emit comment - why change was made to help with troubleshooting issues
- Unity assembly reload can be forced off via LockAssemblyReload if specified in options - sometimes even though Auto-Refresh is turned off Editor still tries to recompile changes in playmode which prevents FSR from working
- new fields support (experimental) will be enabled by default

1.3.1

- open source release on github

1.3 - (Experimental) - New fields added in playmode

- New fields can be added and used in code
- New fields can be adjusted in editor (same as standard fields)
- New fields will be initialized to whatever value is specified in code or default value
- Experimental feature - at this stage expect some issues
- Opt-in - disabled by default to enable go to 'Window -> Fast Script Reload -> Start Screen -> New Fields -> enable'

1.2 - Debugger support

- Added debugger support (see documentation)

1.1 - Mac support / bug fixes

- Added Mac support (only INTEL editor version, SILICON still not supported)
- Added Linux support
- fixed namespace clash with Unity.Collections package
- common code lib will not be included in builds
- added check for auto-refresh in Editor - will proide guidance and option to adjust as otherwise full editor reload is triggered for changes
- added workaround for Unity file-watcher returning wrong file path on some editor versions
- added option to allow disabling DidFieldCountCheck - allowing to detour methods in those cases (eg for Mirror where it'll adjust IL and cause mismatch)
- added option to configure FileWatcher paths/filters as in some cases watching root directory was causing performance issues
- added minor initial-load optimisations - using session-state for items that do not need to be resolved on every reload

1.0 - First release, included features:

Fast script reload in editor / play-mode (compiles only changed files and hot-reloads them into current play session)
Hot-reload on device (in build)