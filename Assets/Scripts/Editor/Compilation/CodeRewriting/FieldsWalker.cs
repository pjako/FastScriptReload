using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace FastScriptReload.Editor.Compilation.CodeRewriting
{
    class FieldsWalker : CSharpSyntaxWalker {
        private readonly Dictionary<string, List<NewFieldDeclaration>> _typeNameToFieldDeclarations = new Dictionary<string, List<NewFieldDeclaration>>();

        public override void VisitClassDeclaration(ClassDeclarationSyntax node)
        {
            var className = node.Identifier;
            var fullClassName = RoslynUtils.GetMemberFQDN(node, className.ToString());
            if(!_typeNameToFieldDeclarations.ContainsKey(fullClassName)) {
                _typeNameToFieldDeclarations[fullClassName] = new List<NewFieldDeclaration>();
            }

            base.VisitClassDeclaration(node);
        }

        public override void VisitFieldDeclaration(FieldDeclarationSyntax node)
        {
            //Only instance fields need their values kept outside the object, a static field added to the patched type is just used from there
            if (node.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword) || m.IsKind(SyntaxKind.ConstKeyword)))
            {
                base.VisitFieldDeclaration(node);
                return;
            }

            var fieldName = node.Declaration.Variables.First().Identifier.ToString();
            var fullClassName = RoslynUtils.GetMemberFQDNWithoutMemberName(node);
            if(!_typeNameToFieldDeclarations.ContainsKey(fullClassName)) {
                _typeNameToFieldDeclarations[fullClassName] = new List<NewFieldDeclaration>();
            }
		
            _typeNameToFieldDeclarations[fullClassName].Add(new NewFieldDeclaration(fieldName, node.Declaration.Type.ToString(), node));
		
            base.VisitFieldDeclaration(node);
        }

        public Dictionary<string, List<NewFieldDeclaration>> GetTypeToFieldDeclarations() {
            return _typeNameToFieldDeclarations;
        }
        
        public List<string> GetTypeNames() {
            return _typeNameToFieldDeclarations.Select(kv => kv.Key).ToList();
        }
    }
}