using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;

namespace FastScriptReload.Editor.Compilation
{
    /// <summary>
    /// Compiles hot reload assemblies with the Roslyn that's already loaded for code rewriting, instead of starting a dotnet process for every change.
    /// Metadata references are cached, so Roslyn can reuse what it read from referenced assemblies across compilations.
    /// </summary>
    public static class InProcessRoslynCompilation
    {
        // Same warnings the dotnet compiler is run without, they're expected in rewritten code
        private static readonly Dictionary<string, ReportDiagnostic> SuppressedWarnings = new[] { "CS0169", "CS0649", "CS1701", "CS1702" }
            .ToDictionary(id => id, _ => ReportDiagnostic.Suppress);
        private static readonly CultureInfo MessagesCulture = CultureInfo.GetCultureInfo("en-US");

        private static readonly Func<CSharpCompilationOptions, CSharpCompilationOptions> WithIgnoreAccessibility = CreateWithIgnoreAccessibility();

        // FSR's Harmony embeds public copies of types like ReadOnlySpan<T>, they clash with mscorlib's (Unity 6.6+).
        // Behind an alias its types are only visible through 'extern alias', so code not using Harmony doesn't see them
        private const string HarmonyFileName = "0Harmony.dll";
        public const string HarmonyReferenceAlias = "FastScriptReloadHarmony";

        public static bool IsReferenceAliased(string referencePath, bool sourceUsesHarmony)
        {
            return !sourceUsesHarmony && string.Equals(Path.GetFileName(referencePath), HarmonyFileName, StringComparison.OrdinalIgnoreCase);
        }

        public static bool UsesHarmony(string sourceCode)
        {
            return sourceCode.Contains("HarmonyLib");
        }

        /// <summary>
        /// Code can use internal and private members of other assemblies without them being made visible first.
        /// Mono doesn't enforce member access at runtime, so this only lifts the compiler's checks.
        /// </summary>
        public static bool CanIgnoreAccessibility => WithIgnoreAccessibility != null;

        // Compilations can run concurrently on background threads
        private static readonly object ReferenceCacheLock = new object();
        private static readonly Dictionary<string, CachedReference> ReferenceCache = new Dictionary<string, CachedReference>();

        // Uses Unity's API and common language features, every part of the compiler it reaches is JIT compiled during warm-up
        private const string WarmUpSourceCode = @"
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace FastScriptReloadWarmUp
{
    public enum Mode { First, Second }
    public delegate Vector3 Function(float u, float v);
    public struct Data { public int Value; public Data(int value) { Value = value; } }

    public class WarmUp : MonoBehaviour
    {
        [SerializeField] private Transform target;
        private readonly List<Data> items = new List<Data>();
        public float Speed { get; set; } = 1f;

        private void Update()
        {
            Function function = (u, v) => new Vector3(Mathf.Sin(u), v, Mathf.Cos(u * Time.time));
            foreach (var item in items.Where(i => i.Value > 0).OrderBy(i => i.Value))
            {
                target.localPosition = function(item.Value, Speed);
            }

            var mode = Speed > 1f ? Mode.First : Mode.Second;
            switch (mode)
            {
                case Mode.First: Debug.Log($""{name}: {Speed:0.0}""); break;
                default: Debug.LogWarning(mode.ToString()); break;
            }
        }

        private static T Pick<T>(T a, T b) where T : IComparable<T> => a.CompareTo(b) > 0 ? a : b;
    }
}";

        public static int Compile(string assemblyName, string outLibraryPath, IEnumerable<SourceFile> sourceFiles, IEnumerable<string> preprocessorSymbols,
            IEnumerable<string> referencePaths, bool allowUnsafe, out List<string> outputMessages)
        {
            var compilation = CreateCompilation(assemblyName, sourceFiles, preprocessorSymbols, referencePaths, allowUnsafe);
            var pdbPath = Path.ChangeExtension(outLibraryPath, ".pdb");

            using (var peStream = new MemoryStream())
            using (var pdbStream = new MemoryStream())
            {
                var result = compilation.Emit(peStream, pdbStream, options: CreateEmitOptions(pdbPath));
                outputMessages = result.Diagnostics
                    .Where(d => d.Severity >= DiagnosticSeverity.Warning && !d.IsSuppressed)
                    .Select(d => CSharpDiagnosticFormatter.Instance.Format(d, MessagesCulture))
                    .ToList();

                if (!result.Success)
                {
                    throw new Exception("Compiler failed to produce the assembly. Output: '" +
                                        string.Join(Environment.NewLine + Environment.NewLine, outputMessages) + "'");
                }

                // Written only after a successful emit, a failed compilation leaves no partial assembly behind
                File.WriteAllBytes(outLibraryPath, peStream.ToArray());
                File.WriteAllBytes(pdbPath, pdbStream.ToArray());
                return 0;
            }
        }

        /// <summary>
        /// Compiles a small snippet in memory, so the first hot reload doesn't have to wait for Roslyn's code being JIT compiled
        /// and referenced assemblies being read. Both are one-off costs that took a few seconds.
        /// </summary>
        public static void WarmUp(IEnumerable<string> preprocessorSymbols, IEnumerable<string> referencePaths)
        {
            var sourceFiles = new[] { new SourceFile("FastScriptReloadWarmUp.cs", WarmUpSourceCode) };
            var compilation = CreateCompilation("FastScriptReloadWarmUp", sourceFiles, preprocessorSymbols, referencePaths, false);

            using (var peStream = new MemoryStream())
            using (var pdbStream = new MemoryStream())
            {
                compilation.Emit(peStream, pdbStream, options: CreateEmitOptions("FastScriptReloadWarmUp.pdb"));
            }
        }

        private static CSharpCompilation CreateCompilation(string assemblyName, IEnumerable<SourceFile> sourceFiles, IEnumerable<string> preprocessorSymbols,
            IEnumerable<string> referencePaths, bool allowUnsafe)
        {
            var parseOptions = new CSharpParseOptions(LanguageVersion.Latest, preprocessorSymbols: preprocessorSymbols);
            // Path and encoding end up in the pdb, so breakpoints can be set in the generated files
            var syntaxTrees = sourceFiles
                .Select(f => CSharpSyntaxTree.ParseText(f.SourceCode, parseOptions, f.Path, Encoding.UTF8))
                .ToList();
            var compilationOptions = new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: OptimizationLevel.Debug,
                allowUnsafe: allowUnsafe,
                deterministic: true,
                specificDiagnosticOptions: SuppressedWarnings);
            if (CanIgnoreAccessibility)
            {
                // Non-public members are only imported from referenced assemblies with MetadataImportOptions.All
                compilationOptions = WithIgnoreAccessibility(compilationOptions.WithMetadataImportOptions(MetadataImportOptions.All));
            }

            var sourceUsesHarmony = sourceFiles.Any(f => UsesHarmony(f.SourceCode));
            var references = referencePaths.Select(path => IsReferenceAliased(path, sourceUsesHarmony)
                ? GetReference(path).WithAliases(new[] { HarmonyReferenceAlias })
                : GetReference(path));
            return CSharpCompilation.Create(assemblyName, syntaxTrees, references, compilationOptions);
        }

        private static EmitOptions CreateEmitOptions(string pdbPath)
        {
            return new EmitOptions(
                debugInformationFormat: DebugInformationFormat.PortablePdb,
                pdbFilePath: pdbPath,
                runtimeMetadataVersion: "v4.0.30319");
        }

        // Roslyn has no public option for this, it's the internal BinderFlags.IgnoreAccessibility (also used by assembly publicizers)
        private static Func<CSharpCompilationOptions, CSharpCompilationOptions> CreateWithIgnoreAccessibility()
        {
            var binderFlagsType = typeof(CSharpCompilationOptions).Assembly.GetType("Microsoft.CodeAnalysis.CSharp.BinderFlags");
            var withTopLevelBinderFlags = typeof(CSharpCompilationOptions).GetMethod("WithTopLevelBinderFlags", BindingFlags.NonPublic | BindingFlags.Instance);
            if (binderFlagsType == null || withTopLevelBinderFlags == null || !Enum.IsDefined(binderFlagsType, "IgnoreAccessibility"))
            {
                return null;
            }

            var ignoreAccessibility = Enum.Parse(binderFlagsType, "IgnoreAccessibility");
            return options => (CSharpCompilationOptions)withTopLevelBinderFlags.Invoke(options, new[] { ignoreAccessibility });
        }

        private static MetadataReference GetReference(string path)
        {
            var lastWriteTimeUtc = File.GetLastWriteTimeUtc(path);
            lock (ReferenceCacheLock)
            {
                // Assemblies with InternalsVisibleTo added are rewritten in place, the timestamp tells when a cached reference is stale
                if (ReferenceCache.TryGetValue(path, out var cached) && cached.LastWriteTimeUtc == lastWriteTimeUtc)
                {
                    return cached.Reference;
                }

                var reference = MetadataReference.CreateFromFile(path);
                ReferenceCache[path] = new CachedReference(lastWriteTimeUtc, reference);
                return reference;
            }
        }

        private class CachedReference
        {
            public DateTime LastWriteTimeUtc { get; }
            public MetadataReference Reference { get; }

            public CachedReference(DateTime lastWriteTimeUtc, MetadataReference reference)
            {
                LastWriteTimeUtc = lastWriteTimeUtc;
                Reference = reference;
            }
        }

        public class SourceFile
        {
            public string Path { get; }
            public string SourceCode { get; }

            public SourceFile(string path, string sourceCode)
            {
                Path = path;
                SourceCode = sourceCode;
            }
        }
    }
}
