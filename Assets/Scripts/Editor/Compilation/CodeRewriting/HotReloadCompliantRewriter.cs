using System.Collections.Generic;
using System.Linq;
using FastScriptReload.Runtime;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace FastScriptReload.Editor.Compilation.CodeRewriting
{
    class HotReloadCompliantRewriter : FastScriptReloadCodeRewriterBase
    {
        public List<string> StrippedUsingDirectives = new List<string>();
        public List<string> StrippedAssemblyAttributes = new List<string>();
        public List<string> OriginalIdentifiersRenamedToContainPatchedPostfix = new List<string>();

        public HotReloadCompliantRewriter(bool writeRewriteReasonAsComment, bool visitIntoStructuredTrivia = false) 
            : base(writeRewriteReasonAsComment, visitIntoStructuredTrivia)
        {
        }

        public override SyntaxNode VisitClassDeclaration(ClassDeclarationSyntax node)
        {
            return AddPatchedPostfixToTopLevelDeclarations(node, node.Identifier);
            //if subclasses need to be adjusted, it's done via recursion.
            // foreach (var childNode in node.ChildNodes().OfType<ClassDeclarationSyntax>())
            // {
            //     var changed = Visit(childNode);
            //     node = node.ReplaceNode(childNode, changed);
            // }
        }

        public override SyntaxNode VisitStructDeclaration(StructDeclarationSyntax node)
        {
            return AddPatchedPostfixToTopLevelDeclarations(node, node.Identifier);
        }

        public override SyntaxNode VisitEnumDeclaration(EnumDeclarationSyntax node)
        {
            return AddPatchedPostfixToTopLevelDeclarations(node, node.Identifier);
        }

        public override SyntaxNode VisitDelegateDeclaration(DelegateDeclarationSyntax node)
        {
            return AddPatchedPostfixToTopLevelDeclarations(node, node.Identifier);
        }

        public override SyntaxNode VisitInterfaceDeclaration(InterfaceDeclarationSyntax node)
        {
            return AddPatchedPostfixToTopLevelDeclarations(node, node.Identifier);
        }

        public override SyntaxNode VisitCompilationUnit(CompilationUnitSyntax node)
        {
            var usings = node.Usings;
            var attributeLists = node.AttributeLists;
            if (usings.Count == 0 && attributeLists.Count == 0) return base.VisitCompilationUnit(node);

            foreach (var usingDirective in usings) StrippedUsingDirectives.Add(usingDirective.ToFullString());

            // Assembly and module attributes (a file's own, or the InternalsVisibleTo a merged partial gets) have to come
            // before every type of the combined file, which is the files one after another: left in place, the second file's
            // were behind the first file's types (CS1730). They move up behind the usings, their trivia (comments, directives) stays
            foreach (var attributeList in attributeLists) StrippedAssemblyAttributes.Add(attributeList.WithoutTrivia().ToFullString() + "\n");

            // The usings move to the top of the combined file; their lines stay behind as blank lines, so the
            // '#line 1 "<original file>"' mapping still matches line for line (stack traces, breakpoints)
            var padding = Enumerable.Repeat(SyntaxFactory.EndOfLine("\n"), usings.ToFullString().Count(c => c == '\n')).ToList();
            foreach (var attributeList in attributeLists)
            {
                padding.AddRange(attributeList.GetLeadingTrivia());
                padding.AddRange(Enumerable.Repeat(SyntaxFactory.EndOfLine("\n"), attributeList.WithoutLeadingTrivia().ToFullString().Count(c => c == '\n')));
            }
            var stripped = node.WithUsings(default).WithAttributeLists(default);
            var firstToken = stripped.GetFirstToken(includeZeroWidth: true);
            stripped = stripped.ReplaceToken(firstToken, firstToken.WithLeadingTrivia(padding.Concat(firstToken.LeadingTrivia)));
            return base.VisitCompilationUnit(stripped);
        }

        private SyntaxNode AddPatchedPostfixToTopLevelDeclarations(MemberDeclarationSyntax node, SyntaxToken identifier)
        {
            // Only types that are already loaded get the postfix: references in the file keep binding to the original type.
            // A new type has no original, renaming it left every reference to it unresolved (CS0246) so the file never compiled
            if (RoslynUtils.IsNewType(node))
            {
                return node;
            }

            OriginalIdentifiersRenamedToContainPatchedPostfix.Add(identifier.ValueText);
            
            // keeps the identifier's trivia: dropping its trailing newline would shift every following line ('#line' mapping)
            var newIdentifier = SyntaxFactory.Identifier(identifier.LeadingTrivia, identifier.ValueText + AssemblyChangesLoader.ClassnamePatchedPostfix, identifier.TrailingTrivia);
            newIdentifier = AddRewriteCommentIfNeeded(newIdentifier, $"{nameof(HotReloadCompliantRewriter)}:{nameof(AddPatchedPostfixToTopLevelDeclarations)}");
            node = node.ReplaceToken(identifier, newIdentifier);
            return node;
        }
    }
}