using System;
using System.Linq;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Management.Automation.Language;
using System.Text.RegularExpressions;

namespace PSClassy.Core
{
    using static SyntaxFactory;
    public class Generator
    {
        private TypeDefinitionAst _ast;
        private readonly SortedSet<UsingDirectiveSyntax> _usingDirectives;
        private static Func<Ast, bool> s_typeConstraintFilter = ast => ast is TypeConstraintAst;

        private Generator(TypeDefinitionAst ast, UsingStatementAst[] usings = null)
        {
            _ast = ast;
            // We only need one using directive per distinct namespace
            _usingDirectives = new SortedSet<UsingDirectiveSyntax>(
                Comparer<UsingDirectiveSyntax>.Create(
                    (a,b) => a.Name.ToString().CompareTo(b.Name.ToString())
                )
            );

            if (usings is UsingStatementAst[])
                foreach (var usingStmt in usings)
                    _usingDirectives.Add(UsingDirective(IdentifierName(usingStmt.Name.Value)));
        }

        private ClassDeclarationSyntax GenerateClassDeclaration()
        {
            return ClassDeclaration(_ast.Name)
                .AddModifiers(Token(SyntaxKind.PublicKeyword));
        }

        private NameSyntax ConvertTypeName(ITypeName typeName)
        {
            if (!typeName.IsGeneric)
            {
                var fullName = typeName.FullName;
                var name = fullName;
                if (fullName.LastIndexOf(".") >= 0)
                {
                    name = fullName.Substring(fullName.LastIndexOf(".") + 1);
                    _usingDirectives.Add(
                        UsingDirective(
                            IdentifierName(
                                fullName.Remove(fullName.LastIndexOf("."))
                            )
                        )
                    );
                }

                return IdentifierName(name);
            }

            // it's generic, extract name for generic type definition and recurse through concrete type arguments
            var genericTypeName = typeName as GenericTypeName;
            return GenericName(
                ConvertTypeName(genericTypeName.TypeName).GetFirstToken(), 
                TypeArgumentList(
                    SeparatedList(
                        genericTypeName.GenericArguments.Select(ConvertTypeName).Cast<TypeSyntax>()
                    )
                )
            );
        }

        private MemberDeclarationSyntax[] GenerateProperties()
        {
            return _ast.Members.Where(m => m is PropertyMemberAst).Cast<PropertyMemberAst>().Select(
                property =>
                {
                    return PropertyDeclaration(ConvertTypeName(property.PropertyType.TypeName), Identifier(property.Name))
                        // Opinionated mutation: "hidden" properties are treated as (assembly-)internal by default
                        .WithModifiers(TokenList(Token(property.IsHidden ? SyntaxKind.InternalKeyword : SyntaxKind.PublicKeyword)))
                        .AddAccessorListAccessors(
                            AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
                                .WithSemicolonToken(Token(SyntaxKind.SemicolonToken)),
                            AccessorDeclaration(SyntaxKind.SetAccessorDeclaration)
                                .WithSemicolonToken(Token(SyntaxKind.SemicolonToken))
                            ).WithoutTrivia();
                }).ToArray();
        }

        private MemberDeclarationSyntax[] GenerateMethods()
        {
            return _ast.Members.Where(m => m is FunctionMemberAst).Cast<FunctionMemberAst>().Select(
                method =>
                {
                    var modfiers = TokenList(Token(method.IsHidden ? SyntaxKind.InternalKeyword : SyntaxKind.PublicKeyword));
                    if (method.IsStatic)
                        modfiers = TokenList(
                            modfiers[0],
                            Token(SyntaxKind.StaticKeyword)
                            );

                    return MethodDeclaration(method.ReturnType is object ? ConvertTypeName(method.ReturnType.TypeName) : PredefinedType(Token(SyntaxKind.VoidKeyword)), Identifier(method.Name))
                        .WithModifiers(
                            modfiers
                        )
                        .WithParameterList(
                            GenerateMethodParameterList(method.Parameters)
                        )
                        .AddBodyStatements(
                            ThrowStatement(
                                ObjectCreationExpression(IdentifierName(typeof(NotImplementedException).Name))
                                    .WithArgumentList(ArgumentList())
                            )
                            .WithLeadingTrivia(
                                GenerateMethodBodyBlockComment(method)
                            )
                        );
                }).ToArray();
        }

        private SyntaxTriviaList GenerateMethodBodyBlockComment(FunctionMemberAst methodAst)
        {
            var inlineComments =
               new List<SyntaxTrivia>
               {
                    Comment("/* Generated from PowerShell class method:"),
                    Comment(" * ")
               };
            inlineComments.AddRange(Regex.Split(methodAst.Extent.Text, "\r?\n").Select((s, i) => Comment($" * {new string(' ', i == 0 ? methodAst.Extent.StartColumnNumber - 1 : 0)}{s}")));
            inlineComments.Add(Comment(" * "));
            inlineComments.Add(Comment(" */"));
            
            return TriviaList(inlineComments);
        }

        private ParameterListSyntax GenerateMethodParameterList(IEnumerable<ParameterAst> parameters)
        {
            return ParameterList(
                SeparatedList(
                    parameters.Select(
                        p => Parameter(Identifier(p.Name.VariablePath.UserPath))
                                .WithType(
                                    (p.Attributes.Count > 0 && p.Attributes.Any(s_typeConstraintFilter))
                                        ? ConvertTypeName(((TypeConstraintAst)p.Attributes.First(s_typeConstraintFilter)).TypeName)
                                        : IdentifierName("object")))
                )
            );
        }

        public static string GetCSharpClassDefinition(TypeDefinitionAst ast, UsingStatementAst[] usings, string indentation, string newline)
        {
            if (!(ast is TypeDefinitionAst))
                throw new ArgumentNullException("ast");

            if (!ast.IsClass)
                throw new ArgumentException("The type definition provided is not of a class", nameof(ast));

            if (null == indentation)
                indentation = new string(' ', 4);

            if (null == newline)
                newline = Environment.NewLine;

            var codeGenerator = new Generator(ast, usings);

            return CompilationUnit()
                .AddMembers(
                    codeGenerator.GenerateClassDeclaration()
                        .AddMembers(
                            codeGenerator.GenerateProperties()
                        )
                        .AddMembers(
                            codeGenerator.GenerateMethods()
                        )
                )
                .AddUsings(
                    codeGenerator._usingDirectives.ToArray()
                )
                .NormalizeWhitespace(indentation, newline, false)
                .ToFullString();
        }
    }
}
