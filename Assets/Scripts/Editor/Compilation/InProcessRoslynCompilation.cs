using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
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

        // Compilations can run concurrently on background threads
        private static readonly object ReferenceCacheLock = new object();
        private static readonly Dictionary<string, CachedReference> ReferenceCache = new Dictionary<string, CachedReference>();

        public static int Compile(string assemblyName, string outLibraryPath, IEnumerable<SourceFile> sourceFiles, IEnumerable<string> preprocessorSymbols,
            IEnumerable<string> referencePaths, bool allowUnsafe, out List<string> outputMessages)
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
            var compilation = CSharpCompilation.Create(assemblyName, syntaxTrees, referencePaths.Select(GetReference), compilationOptions);

            var pdbPath = Path.ChangeExtension(outLibraryPath, ".pdb");
            var emitOptions = new EmitOptions(
                debugInformationFormat: DebugInformationFormat.PortablePdb,
                pdbFilePath: pdbPath,
                runtimeMetadataVersion: "v4.0.30319");

            using (var peStream = new MemoryStream())
            using (var pdbStream = new MemoryStream())
            {
                var result = compilation.Emit(peStream, pdbStream, options: emitOptions);
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
