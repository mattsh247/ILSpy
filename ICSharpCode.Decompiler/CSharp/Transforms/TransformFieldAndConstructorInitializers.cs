// Copyright (c) 2011 AlphaSierraPapa for the SharpDevelop Team
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy of this
// software and associated documentation files (the "Software"), to deal in the Software
// without restriction, including without limitation the rights to use, copy, modify, merge,
// publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons
// to whom the Software is furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in all copies or
// substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
// INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
// PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE
// FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
// OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.

using System.Collections.Generic;
using System.Linq;
using System.Reflection;

using ICSharpCode.Decompiler.CSharp.Syntax;
using ICSharpCode.Decompiler.CSharp.Syntax.PatternMatching;
using ICSharpCode.Decompiler.IL;
using ICSharpCode.Decompiler.TypeSystem;

namespace ICSharpCode.Decompiler.CSharp.Transforms
{
	/// <summary>
	/// This transform moves field initializers at the start of constructors to their respective field declarations
	/// and transforms this-/base-ctor calls in constructors to constructor initializers.
	/// </summary>
	public class TransformFieldAndConstructorInitializers : DepthFirstAstVisitor, IAstTransform
	{
		record MemberTransformInfo(IMember Member, AstNode Syntax)
		{
			public List<Statement> StatementsToRemove { get; set; } = new List<Statement>();
			public Expression Initializer { get; set; }
			public bool CanMoveInitializer { get; set; } = true;
			public RecordDecompiler Record { get; set; }
			public bool IsPrimaryCtor { get; set; }

			public IParameter AssociatedPrimaryConstructorParameter { get; set; }

			public bool IsCompilerGeneratedPrimaryConstructorParameterBackingField()
			{
				return Member.SymbolKind == SymbolKind.Field
					&& Member.Name.StartsWith("<", System.StringComparison.Ordinal)
					&& Member.Name.EndsWith(">P", System.StringComparison.Ordinal)
					&& Member.IsCompilerGenerated();
			}

			public bool IsMemberDeclaredByPrimaryConstructor()
			{
				if (Record != null)
				{
					if (Member is IProperty p && Record.IsPropertyDeclaredByPrimaryConstructor(p))
						return true;
				}
				else if (IsCompilerGeneratedPrimaryConstructorParameterBackingField())
				{
					return true;
				}

				return false;
			}

			public bool CanMoveExpressionToTypeLevel()
			{
				if (Initializer == null)
					return false;

				var instruction = Initializer.Annotation<ILInstruction>();

				if (instruction == null)
					return true;

				foreach (var i in instruction.Descendants)
				{
					if (i is ILoadInstruction load)
					{
						var f = load.Variable.Function.Method;
						if (load.Variable.Kind.IsLocal())
						{
							if (f.IsConstructor)
							{
								return false;
							}
						}
					}
				}

				return Member.DeclaringTypeDefinition.Kind != TypeKind.Struct || Record?.PrimaryConstructor != null;
			}
		}

		record AnalysisResult(Dictionary<IMember, MemberTransformInfo> TransformInfo);

		static readonly ExpressionStatement fieldInitializerPattern = new ExpressionStatement {
			Expression = new AssignmentExpression {
				Left = new Choice {
					new NamedNode("fieldAccess", new MemberReferenceExpression {
										 Target = new ThisReferenceExpression(),
										 MemberName = Pattern.AnyString
									 }),
					new NamedNode("fieldAccess", new IdentifierExpression(Pattern.AnyString))
				},
				Operator = AssignmentOperatorType.Assign,
				Right = new AnyNode("initializer")
			}
		};

		static readonly ExpressionStatement ctorCallPattern = new ExpressionStatement(
			new Choice {
				new InvocationExpression(new MemberReferenceExpression(new Choice {
					new ThisReferenceExpression(),
					new BaseReferenceExpression(),
					new CastExpression(new AnyNode(), new ThisReferenceExpression()),
				}, ".ctor"), new Repeat(new AnyNode())),
				new AssignmentExpression(new ThisReferenceExpression(), new ObjectCreateExpression(new AnyNode("type"), new Repeat(new AnyNode())))
			}
		);

		TransformContext context;

		public void Run(AstNode node, TransformContext context)
		{
			this.context = context;

			try
			{
				var result = Analyze(node.Children, context.CurrentTypeDefinition);
				ApplyTransform(result);

				node.AcceptVisitor(this);
			}
			finally
			{
				this.context = null;
			}
		}

		AnalysisResult Analyze(IEnumerable<AstNode> nodes, ITypeDefinition currentType)
		{
			// Set up a list of members to process
			Dictionary<IMember, MemberTransformInfo> info = new();
			foreach (var node in nodes)
			{
				if (node.GetSymbol() is not IEntity member)
					continue;
				MemberTransformInfo i;
				switch (member)
				{
					case IField _:
					case IEvent _ when node is not CustomEventDeclaration:
						i = new MemberTransformInfo((IMember)member, node) {
							Initializer = node.GetChildByRole(Roles.Variable).Initializer
						};
						if (context.DecompileRun.RecordDecompilers.TryGetValue(member.DeclaringTypeDefinition, out var r))
						{
							i.Record = r;
						}
						info.Add((IMember)member, i);
						break;
					case IProperty _:
						i = new MemberTransformInfo((IMember)member, node) {
							Initializer = node.GetChildByRole(Roles.Expression)
						};
						if (context.DecompileRun.RecordDecompilers.TryGetValue(member.DeclaringTypeDefinition, out r))
						{
							i.Record = r;
						}
						info.Add((IMember)member, i);
						break;
					case IMethod m when m.IsConstructor:
						i = new MemberTransformInfo((IMember)member, node);
						if (context.DecompileRun.RecordDecompilers.TryGetValue(member.DeclaringTypeDefinition, out r))
						{
							i.Record = r;
							i.IsPrimaryCtor = r.PrimaryConstructor == m;
						}
						info.Add((IMember)member, i);
						break;
				}
			}

			if (currentType != null && context.DecompileRun.RecordDecompilers.TryGetValue(currentType, out var record))
			{
				foreach (var item in record.FieldsAndProperties)
				{
					if (item is IProperty p && record.IsPropertyDeclaredByPrimaryConstructor(p))
					{
						info.Add(item.MemberDefinition, new MemberTransformInfo(item, null) { Record = record });
					}
				}
			}

			if (info.Count == 0)
			{
				return null;
			}

			// Analyze constructors
			foreach (var item in info.Values)
			{
				if (item.Member is IMethod ctor)
				{
					bool isMarkedBeforeFieldInit = ctor.DeclaringTypeDefinition.GetMetadataAttributes().HasFlag(TypeAttributes.BeforeFieldInit);
					var statements = ((ConstructorDeclaration)item.Syntax).Body.Statements;
					foreach (var stmt in statements)
					{
						var m = fieldInitializerPattern.Match(stmt);
						if (m.Success)
						{
							IMember member = (m.Get<AstNode>("fieldAccess").Single().GetSymbol() as IMember)?.MemberDefinition;
							if (!info.TryGetValue(member, out var memberInfo))
							{
								break;
							}
							Expression initializer = m.Get<Expression>("initializer").Single();
							if (memberInfo.Initializer == null || memberInfo.Initializer.IsNull)
							{
								memberInfo.Initializer = initializer;
							}
							else if (!memberInfo.Initializer.IsMatch(initializer))
							{
								return null;
							}

							if (initializer.GetResolveResult() is ILVariableResolveResult { Variable: { Kind: VariableKind.Parameter, Index: var index, Function.Method: var method } }
								&& method == ctor && index >= 0 && index < method.Parameters.Count)
							{
								memberInfo.AssociatedPrimaryConstructorParameter = method.Parameters[index.Value];
								item.IsPrimaryCtor = true;
							}

							if (memberInfo.IsMemberDeclaredByPrimaryConstructor())
							{

							}
							else if (!memberInfo.CanMoveExpressionToTypeLevel())
							{
								memberInfo.CanMoveInitializer = false;
								break;
							}

							// in static ctors we only move constants, if the the type is marked beforefieldinit
							if (ctor.IsStatic && !isMarkedBeforeFieldInit && member is not IField { IsConst: true })
							{
								memberInfo.CanMoveInitializer = false;
							}
							else
							{
								memberInfo.StatementsToRemove.Add(stmt);
							}
							continue;
						}
						m = ctorCallPattern.Match(stmt);
						if (!m.Success)
						{
							break;
						}
						var expr = ((ExpressionStatement)stmt).Expression;
						ISymbol symbol;
						if (expr is AssignmentExpression { Right: ObjectCreateExpression oce })
						{
							// Pattern for value types:
							// this = new TSelf(...);
							symbol = oce.GetSymbol();
							if (symbol is not IMethod { DeclaringTypeDefinition: var type } || type != item.Member.DeclaringTypeDefinition)
							{
								return null;
							}
						}
						else
						{
							// Pattern for reference types:
							// this..ctor(...);
							symbol = expr.GetSymbol();
						}
						if (symbol is not IMethod { IsConstructor: true })
						{
							return null;
						}

						item.Initializer = expr;
						break;
					}
				}
			}

			return new AnalysisResult(info);
		}

		void ApplyTransform(AnalysisResult result)
		{
			if (result == null)
				return;

			foreach (var item in result.TransformInfo.Values)
			{
				if (item.Initializer == null || item.Initializer.IsNull || !item.CanMoveInitializer)
				{
					continue;
				}

				switch (item.Syntax)
				{
					case FieldDeclaration field:
						RemoveStatements(item);
						field.Variables.Single().Initializer = item.Initializer.Detach();
						if (IntroduceUnsafeModifier.IsUnsafe(item.Initializer))
						{
							field.Modifiers |= Modifiers.Unsafe;
						}
						break;
					case PropertyDeclaration property:
						RemoveStatements(item);
						property.Initializer = item.Initializer.Detach();
						break;
					case EventDeclaration eventDeclaration:
						RemoveStatements(item);
						eventDeclaration.Variables.Single().Initializer = item.Initializer.Detach();
						break;
					case ConstructorDeclaration constructorDeclaration:
						ConstructorInitializer ci;
						switch (item.Initializer)
						{
							case InvocationExpression { Target: MemberReferenceExpression } invocation:
								ci = new ConstructorInitializer();
								var ctor = (IMethod)invocation.GetSymbol();
								if (ctor.DeclaringTypeDefinition == item.Member.DeclaringTypeDefinition)
									ci.ConstructorInitializerType = ConstructorInitializerType.This;
								else
									ci.ConstructorInitializerType = ConstructorInitializerType.Base;
								// Move arguments from invocation to initializer:
								invocation.Arguments.MoveTo(ci.Arguments);
								// Add the initializer: (unless it is the default 'base()')
								if (!(ci.ConstructorInitializerType == ConstructorInitializerType.Base && ci.Arguments.Count == 0))
									constructorDeclaration.Initializer = ci.CopyAnnotationsFrom(invocation);
								// Remove the statement:
								item.Initializer.Parent.Remove();
								break;
							case AssignmentExpression { Right: ObjectCreateExpression oce }:
								ci = new ConstructorInitializer();
								ci.ConstructorInitializerType = ConstructorInitializerType.This;
								// Move arguments from invocation to initializer:
								oce.Arguments.MoveTo(ci.Arguments);
								// Add the initializer:
								constructorDeclaration.Initializer = ci.CopyAnnotationsFrom(oce);
								// Remove the statement:
								item.Initializer.Parent.Remove();
								break;
						}
						break;
					case null when item.Record != null:
						RemoveStatements(item);
						break;
				}
			}

			MemberTransformInfo staticCtor = null;
			List<MemberTransformInfo> ctors = new();
			MemberTransformInfo primaryCtor = null;

			foreach (var item in result.TransformInfo.Values)
			{
				if (item.Member is not IMethod { IsConstructor: true } ctor || item.Syntax is not ConstructorDeclaration declaration)
				{
					continue;
				}

				if (ctor.IsStatic)
				{
					staticCtor = item;
				}
				else
				{
					ctors.Add(item);
					if (item.Record != null)
					{
						if (item.Record.PrimaryConstructor == ctor)
						{
							primaryCtor = item;
						}
					}
					else if (declaration.Initializer?.ConstructorInitializerType != ConstructorInitializerType.This && declaration.Body.Statements.Count == 0 && item.IsPrimaryCtor)
					{
						primaryCtor = item;
					}
				}
			}

			if (staticCtor != null && CanHideConstructor((IMethod)staticCtor.Member, (ConstructorDeclaration)staticCtor.Syntax))
			{
				HideConstructor(staticCtor.Syntax);
			}

			if (ctors.Count == 1 && CanHideConstructor((IMethod)ctors[0].Member, (ConstructorDeclaration)ctors[0].Syntax))
			{
				HideConstructor(ctors[0].Syntax);
			}
			else if (primaryCtor?.Syntax is ConstructorDeclaration { Parent: TypeDeclaration typeDecl } declaration)
			{
				if (primaryCtor.Record != null)
				{
					if (primaryCtor.Record.IsInheritedRecord
						&& declaration.Initializer is { ConstructorInitializerType: ConstructorInitializerType.Base } ci
						&& typeDecl.BaseTypes.Count >= 1)
					{
						var baseType = typeDecl.BaseTypes.First();
						var newBaseType = new InvocationAstType();
						baseType.ReplaceWith(newBaseType);
						newBaseType.BaseType = baseType;
						ci.Arguments.MoveTo(newBaseType.Arguments);
					}
					HideConstructor(declaration);
				}
				else
				{
					declaration.Parameters.MoveTo(typeDecl.PrimaryConstructorParameters);

					if (declaration.Initializer is { ConstructorInitializerType: ConstructorInitializerType.Base } ci
						&& typeDecl.BaseTypes.Count >= 1)
					{
						var baseType = typeDecl.BaseTypes.First();
						var newBaseType = new InvocationAstType();
						baseType.ReplaceWith(newBaseType);
						newBaseType.BaseType = baseType;
						ci.Arguments.MoveTo(newBaseType.Arguments);
					}

					declaration.Remove();

					foreach (var field in result.TransformInfo.Values)
					{
						if (field.IsCompilerGeneratedPrimaryConstructorParameterBackingField())
						{
							field.Syntax.Remove();
							var attributes = field.Member.GetAttributes()
								.Where(a => !PatternStatementTransform.attributeTypesToRemoveFromAutoProperties.Contains(a.AttributeType.FullName))
								.Select(context.TypeSystemAstBuilder.ConvertAttribute).ToArray();
							if (attributes.Length > 0)
							{
								var section = new AttributeSection {
									AttributeTarget = "field"
								};
								section.Attributes.AddRange(attributes);
								foreach (var pd in typeDecl.PrimaryConstructorParameters)
								{
									if (pd.GetSymbol() == field.AssociatedPrimaryConstructorParameter)
										pd.Attributes.Add(section);
								}
							}

							foreach (var identifier in typeDecl.Descendants.OfType<IdentifierExpression>())
							{
								if (identifier.GetSymbol() == field.Member)
								{
									identifier.ReplaceWith(field.Initializer.Clone());
								}
							}
						}
					}
				}
			}

			void HideConstructor(AstNode declaration)
			{
				if (declaration.Parent is not SyntaxTree || declaration.Parent.Children.Skip(1).Any())
				{
					declaration.Remove();
				}
			}

			bool CanHideConstructor(IMethod ctor, ConstructorDeclaration declaration)
			{
				// keep ctor because it is not empty
				if (declaration.Body.Statements.Count != 0)
				{
					return false;
				}

				// keep ctor because there is XMLDoc on the item
				if (context.Settings.ShowXmlDocumentation && context.DecompileRun.DocumentationProvider?.GetDocumentation(ctor) != null)
				{
					return false;
				}

				// keep ctor because of initializer
				if (!declaration.Initializer.IsNull)
				{
					return false;
				}

				// non-empty parameter list, keep the declaration
				if (ctor.Parameters.Count > 0)
				{
					return false;
				}

				if (ctor.IsStatic)
				{
					// keep static ctor because the containing type is not marked beforefieldinit
					bool isMarkedBeforeFieldInit = ctor.DeclaringTypeDefinition.GetMetadataAttributes().HasFlag(TypeAttributes.BeforeFieldInit);
					if (!isMarkedBeforeFieldInit)
					{
						return false;
					}
				}
				else
				{
					// non-default visibility, keep the declaration
					if (ctor.Accessibility != (ctor.DeclaringTypeDefinition.IsAbstract ? Accessibility.Protected : Accessibility.Public))
					{
						return false;
					}
				}

				return true;
			}

			void RemoveStatements(MemberTransformInfo info)
			{
				foreach (var item in info.StatementsToRemove)
				{
					item.Remove();
				}
			}
		}

		public override void VisitTypeDeclaration(TypeDeclaration typeDeclaration)
		{
			var result = Analyze(typeDeclaration.Members, (ITypeDefinition)typeDeclaration.GetSymbol());
			ApplyTransform(result);
			base.VisitTypeDeclaration(typeDeclaration);
		}
	}
}
