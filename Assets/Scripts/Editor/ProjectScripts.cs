using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;
using PackageSource = UnityEditor.PackageManager.PackageSource;

namespace FastScriptReload.Editor
{
    /// <summary>
    /// Scripts that can be hot reloaded, taken from the assemblies Unity compiles: everything in Assets and in embedded / local packages.
    /// Packages from the registry or git can't be edited, Fast Script Reload doesn't reload itself.
    /// </summary>
    public class ProjectScripts
    {
        private const string FastScriptReloadAssemblyPrefix = "FastScriptReload";

        //Read from file watcher threads, never changed after creation
        private readonly HashSet<string> _knownScripts;
        private readonly List<string> _excludedDirectories;

        public IReadOnlyList<string> RootDirectories { get; }

        private ProjectScripts(HashSet<string> knownScripts, List<string> rootDirectories, List<string> excludedDirectories)
        {
            _knownScripts = knownScripts;
            RootDirectories = rootDirectories;
            _excludedDirectories = excludedDirectories;
        }

        /// <summary>Uses Unity APIs, main thread only. Assemblies only change with a domain reload, which creates a new instance</summary>
        public static ProjectScripts Discover()
        {
            var knownScripts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var rootDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { NormalizeDirectory(Application.dataPath) };
            var excludedDirectories = new List<string>();

            foreach (var assembly in CompilationPipeline.GetAssemblies(AssembliesType.Editor))
            {
                if (assembly.sourceFiles.Length == 0)
                {
                    continue;
                }

                //All scripts of an assembly are in the same package (or none, for Assets)
                var package = PackageInfo.FindForAssetPath(assembly.sourceFiles[0]);
                if (package != null && package.source != PackageSource.Embedded && package.source != PackageSource.Local)
                {
                    continue;
                }
                if (package != null)
                {
                    rootDirectories.Add(NormalizeDirectory(package.resolvedPath));
                }

                if (assembly.name.StartsWith(FastScriptReloadAssemblyPrefix))
                {
                    var assemblyDefinitionPath = CompilationPipeline.GetAssemblyDefinitionFilePathFromAssemblyName(assembly.name);
                    if (!string.IsNullOrEmpty(assemblyDefinitionPath))
                    {
                        excludedDirectories.Add(NormalizeDirectory(Path.GetDirectoryName(ToFullPath(assemblyDefinitionPath))));
                    }
                    continue;
                }

                foreach (var sourceFile in assembly.sourceFiles)
                {
                    knownScripts.Add(ToFullPath(sourceFile));
                }
            }

            return new ProjectScripts(knownScripts, rootDirectories.ToList(), excludedDirectories);
        }

        public bool IsAnyScriptChangedSince(DateTime utcTime)
        {
            return _knownScripts.Any(script => File.Exists(script) && File.GetLastWriteTimeUtc(script) > utcTime);
        }

        /// <summary>Thread safe</summary>
        public bool IsHotReloadable(string filePath)
        {
            var fullPath = Path.GetFullPath(filePath);
            if (!fullPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            if (_knownScripts.Contains(fullPath))
            {
                return true;
            }

            //Scripts added since Unity last compiled aren't known yet, they're accepted if Unity would compile them too
            return RootDirectories.Any(root => fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                   && !_excludedDirectories.Any(directory => fullPath.StartsWith(directory, StringComparison.OrdinalIgnoreCase))
                   && !IsInFolderIgnoredByUnity(fullPath);
        }

        //Unity skips folders ending with '~' (eg Samples~) or starting with '.'
        private static bool IsInFolderIgnoredByUnity(string fullPath)
        {
            var folders = Path.GetDirectoryName(fullPath)?.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) ?? Array.Empty<string>();
            return folders.Any(folder => folder.EndsWith("~") || folder.StartsWith("."));
        }

        private static string ToFullPath(string assetPath)
        {
            //Package scripts have virtual paths (Packages/<name>/...), the physical one can be anywhere for local packages
            return Path.GetFullPath(FileUtil.GetPhysicalPath(assetPath));
        }

        private static string NormalizeDirectory(string directory)
        {
            return Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        }
    }
}
