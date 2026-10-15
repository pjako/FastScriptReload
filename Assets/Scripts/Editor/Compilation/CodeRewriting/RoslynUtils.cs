using System;
using System.Linq;
using FastScriptReload.Runtime;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace FastScriptReload.Editor.Compilation.CodeRewriting
{
    public class RoslynUtils
    {
        public static string GetMemberFQDN(MemberDeclarationSyntax memberNode, string memberName) //TODO: try get rid of member name (needs to cast to whatever member it could be to get identifier)
        {
            var outer = GetMemberFQDNWithoutMemberName(memberNode);
            return !string.IsNullOrEmpty(outer)
                ? $"{outer}.{memberName}"
                : memberName;
        }

        public static string GetMemberFQDNWithoutMemberName(MemberDeclarationSyntax memberNode) //TODO: move out to helper class
        {
            var fullTypeContibutingAncestorNames = memberNode.Ancestors().OfType<MemberDeclarationSyntax>().Select(da =>
            {
                if (da is TypeDeclarationSyntax t) return t.Identifier.ToString();
                else if (da is BaseNamespaceDeclarationSyntax n) return n.Name.ToString();
                else throw new Exception("Unable to resolve full field name");
            }).Reverse().ToList();

            return string.Join(".", fullTypeContibutingAncestorNames);
        }

        /// <summary>
        /// True for a type that isn't in the project's loaded assemblies, eg one added since the last full compile.
        /// Such a type has nothing to be patched into, it keeps its name so references to it in the hot reloaded code resolve
        /// </summary>
        public static bool IsNewType(MemberDeclarationSyntax typeNode)
        {
            return !ProjectTypeCache.AllTypesInNonDynamicGeneratedAssemblies.ContainsKey(GetTypeFullName(typeNode));
        }

        //As Type.FullName: namespaces joined by '.', containing types by '+', generic types end in '`<arity>'
        private static string GetTypeFullName(MemberDeclarationSyntax typeNode)
        {
            var typeNames = typeNode.AncestorsAndSelf().TakeWhile(n => !(n is BaseNamespaceDeclarationSyntax)).OfType<MemberDeclarationSyntax>()
                .Select(GetMetadataTypeName)
                .Where(n => n != null)
                .Reverse();
            var namespaces = typeNode.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Select(n => n.Name.ToString()).Reverse().ToList();
            var typeName = string.Join("+", typeNames);
            return namespaces.Count > 0 ? $"{string.Join(".", namespaces)}.{typeName}" : typeName;
        }

        private static string GetMetadataTypeName(MemberDeclarationSyntax node)
        {
            string name;
            int arity;
            switch (node)
            {
                case TypeDeclarationSyntax t: name = t.Identifier.ValueText; arity = t.TypeParameterList?.Parameters.Count ?? 0; break;
                case EnumDeclarationSyntax e: name = e.Identifier.ValueText; arity = 0; break;
                case DelegateDeclarationSyntax d: name = d.Identifier.ValueText; arity = d.TypeParameterList?.Parameters.Count ?? 0; break;
                default: return null;
            }

            //Containing types may already have been renamed by an earlier rewriter
            name = name.Replace(AssemblyChangesLoader.ClassnamePatchedPostfix, string.Empty);
            return arity > 0 ? $"{name}`{arity}" : name;
        }
    }
}