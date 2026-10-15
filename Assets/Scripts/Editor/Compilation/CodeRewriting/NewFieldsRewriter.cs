using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using FastScriptReload.Runtime;
using FastScriptReload.Scripts.Runtime;
using ImmersiveVrToolsCommon.Runtime.Logging;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace FastScriptReload.Editor.Compilation.CodeRewriting
{
	class NewFieldsRewriter : FastScriptReloadCodeRewriterBase
	{
		private readonly Dictionary<string, List<string>> _typeToNewFieldDeclarations;

		public NewFieldsRewriter(Dictionary<string, List<string>> typeToNewFieldDeclarations, bool writeRewriteReasonAsComment) 
			:base(writeRewriteReasonAsComment)
		{
			_typeToNewFieldDeclarations = typeToNewFieldDeclarations;
		}

		public static List<MemberInfo> GetReplaceableMembers(Type type)
		{ //TODO: later other might need to be included? props?
			return type.GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).Cast<MemberInfo>().ToList();
		}


		private static TypeSyntax ResolveFieldType(ClassDeclarationSyntax classNode, string fieldName)
		{
			return classNode.Members.OfType<FieldDeclarationSyntax>()
				.First(f => f.Declaration.Variables.Any(v => v.Identifier.ToString() == fieldName))
				.Declaration.Type.WithoutTrivia();
		}

		public override SyntaxNode VisitInvocationExpression(InvocationExpressionSyntax node)
		{
			if (node.Expression.ToString() == "nameof")
			{
				var classNode = node.Ancestors().OfType<ClassDeclarationSyntax>().FirstOrDefault();
				if (classNode != null)
				{
					var fullClassName = RoslynUtils.GetMemberFQDN(classNode, classNode.Identifier.ToString());
					if (!string.IsNullOrEmpty(fullClassName))
					{
						var nameofExpressionParts = node.ArgumentList.Arguments.First().ToFullString().Split('.'); //nameof could have multiple . like NewFieldCustomClass.FieldInThatClass
						var fieldName = nameofExpressionParts.First();  // should take first part only to determine if new field eg. 'NewFieldCustomClass'
						if (_typeToNewFieldDeclarations.TryGetValue(fullClassName, out var allNewFieldNamesForClass))
						{
							if (allNewFieldNamesForClass.Contains(fieldName))
							{
								return AddRewriteCommentIfNeeded(
									SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal(nameofExpressionParts.Last())), // should take last part only to for actual string eg. 'FieldInThatClass'
									$"{nameof(NewFieldsRewriter)}:{nameof(VisitInvocationExpression)}");
							}
							
						}
					}
				}
			}

			return base.VisitInvocationExpression(node);
		}

		public override SyntaxNode VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
		{
			//this.NewField - whole expression is replaced, 'this' is passed to the accessor
			if (node.Expression is ThisExpressionSyntax && node.Name is IdentifierNameSyntax name 
				&& TryResolveNewField(name, out var classNode, out var fullClassName))
			{
				return AddRewriteCommentIfNeeded(
					CreateNewFieldAccess(classNode, fullClassName, name.Identifier.ToString()).WithTriviaFrom(node),
					$"{nameof(NewFieldsRewriter)}:{nameof(VisitMemberAccessExpression)}"
				);
			}

			return base.VisitMemberAccessExpression(node);
		}

		public override SyntaxNode VisitIdentifierName(IdentifierNameSyntax node)
		{
			//name on the right of member access, eg Time.deltaTime, is not the new field (this.NewField is handled in VisitMemberAccessExpression)
			if (node.Parent is MemberAccessExpressionSyntax memberAccessExpressionSyntax && memberAccessExpressionSyntax.Name == node)
			{
				return base.VisitIdentifierName(node);
			}

			if (TryResolveNewField(node, out var classNode, out var fullClassName))
			{
				return AddRewriteCommentIfNeeded(
					CreateNewFieldAccess(classNode, fullClassName, node.Identifier.ToString()).WithTriviaFrom(node),
					$"{nameof(NewFieldsRewriter)}:{nameof(VisitIdentifierName)}"
				);
			}

			return base.VisitIdentifierName(node);
		}

		private bool TryResolveNewField(IdentifierNameSyntax node, out ClassDeclarationSyntax classNode, out string fullClassName)
		{
			fullClassName = null;
			classNode = node.Ancestors().OfType<ClassDeclarationSyntax>().FirstOrDefault();
			if (classNode == null)
			{
				return false;
			}

			fullClassName = RoslynUtils.GetMemberFQDN(classNode, classNode.Identifier.ToString());
			if (string.IsNullOrEmpty(fullClassName))
			{
				return false;
			}

			if (!_typeToNewFieldDeclarations.TryGetValue(fullClassName, out var allNewFieldNamesForClass))
			{
				LoggerScoped.LogWarning($"Unable to find type: {fullClassName}");
				return false;
			}

			if (!allNewFieldNamesForClass.Contains(node.Identifier.ToString()))
			{
				return false;
			}

			//nameof expression will be rewritten via VisitInvocationExpression
			return !node.Ancestors().OfType<InvocationExpressionSyntax>().Any(e => e.Expression.ToString() == "nameof");
		}

		//eg. TemporaryNewFieldValues.ResolveField<Foo__Patched_, int>(this, "newField"), it returns by ref,
		//so the result is a variable and can still be assigned, incremented and passed as ref / out
		private static ExpressionSyntax CreateNewFieldAccess(ClassDeclarationSyntax classNode, string fullClassName, string fieldName)
		{
			return SyntaxFactory.InvocationExpression(
					SyntaxFactory.MemberAccessExpression(
						SyntaxKind.SimpleMemberAccessExpression,
						SyntaxFactory.IdentifierName(typeof(TemporaryNewFieldValues).FullName),
						SyntaxFactory.GenericName(
								SyntaxFactory.Identifier(nameof(TemporaryNewFieldValues.ResolveField)))
							.WithTypeArgumentList(
								SyntaxFactory.TypeArgumentList(
									SyntaxFactory.SeparatedList<TypeSyntax>(new[]
									{
										SyntaxFactory.IdentifierName(fullClassName + AssemblyChangesLoader.ClassnamePatchedPostfix),
										ResolveFieldType(classNode, fieldName)
									})))))
				.WithArgumentList(
					SyntaxFactory.ArgumentList(
						SyntaxFactory.SeparatedList<ArgumentSyntax>(new[]
						{
							SyntaxFactory.Argument(SyntaxFactory.ThisExpression()),
							SyntaxFactory.Argument(SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal(fieldName)))
						})));
		}

		public override SyntaxNode VisitFieldDeclaration(FieldDeclarationSyntax node)
		{
			var fieldName = node.Declaration.Variables.First().Identifier.ToString();
			var fullClassName = RoslynUtils.GetMemberFQDNWithoutMemberName(node);

			if (_typeToNewFieldDeclarations.TryGetValue(fullClassName, out var newFields))
			{
				if (newFields.Contains(fieldName))
				{
					var existingLeading = node.GetLeadingTrivia();
					var existingTrailing = node.GetTrailingTrivia();

					return AddRewriteCommentIfNeeded(
						node
							.WithLeadingTrivia(existingLeading.Add(SyntaxFactory.Comment("/* ")))
							.WithTrailingTrivia(existingTrailing.Insert(0, SyntaxFactory.Comment(" */ //Auto-excluded to prevent exceptions - see docs"))),
						$"{nameof(NewFieldsRewriter)}:{nameof(VisitFieldDeclaration)}"
					);
				}
			}

			return base.VisitFieldDeclaration(node);
		}
	}
}