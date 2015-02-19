﻿using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.CSharp.Test.Utilities;
using Microsoft.CodeAnalysis.Test.Utilities;
using Xunit;

namespace Microsoft.CodeAnalysis.CSharp.UnitTests.Semantics
{
    public class SuppressAccessibilityChecksTests : CSharpTestBase
    {
        private static SemanticModel GetSemanticModelWithSuppressAccessChecks()
        {
            var compilationA = CreateCompilationWithMscorlib(@"
namespace N
{
    class A
    {
        A M() { return new A(); }
        int _num;
    }
}"
                );
        
            var referenceA = MetadataReference.CreateFromStream(compilationA.EmitToStream());

            var compilationB = CreateCompilationWithMscorlib(@"
using A = N.A;

class B 
{
    void Main() 
    {
        new A().M();
    }
}

", new MetadataReference[] {referenceA}, TestOptions.ReleaseDll.WithMetadataImportOptions(MetadataImportOptions.All));

            var syntaxTree = compilationB.SyntaxTrees[0];
            return compilationB.GetSemanticModel(syntaxTree, suppressAccessChecks: true);
        }

        [Fact]
        public void TestAccessPrivateMemberOfInternalType()
        {
            var semanticModel = GetSemanticModelWithSuppressAccessChecks();
            var invocation = semanticModel.SyntaxTree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>().Single();
            var position = invocation.FullSpan.Start;

            Assert.Equal("A", semanticModel.GetTypeInfo(invocation).Type.Name);
            Assert.Equal("M", semanticModel.GetSymbolInfo(invocation).Symbol.Name);            
            Assert.NotEmpty(semanticModel.LookupSymbols(position, name: "A"));
        }

        [Fact]
        public void TestAccessChecksInSpeculativeExpression()
        {
            var semanticModel = GetSemanticModelWithSuppressAccessChecks();
            var invocation = semanticModel.SyntaxTree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>().Single();
            var position = invocation.FullSpan.Start;

            var exp = SyntaxFactory.ParseExpression("new A().M()._num");
            Assert.Equal("Int32",
                semanticModel.GetSpeculativeTypeInfo(position, exp, SpeculativeBindingOption.BindAsExpression).Type.Name);

            Assert.Equal("_num",
                semanticModel.GetSpeculativeSymbolInfo(position, exp, SpeculativeBindingOption.BindAsExpression).Symbol.Name);
        }

        [Fact]
        public void TestAccessChecksInSpeculativeSemanticModel()
        {
            var semanticModel = GetSemanticModelWithSuppressAccessChecks();
            var invocation = semanticModel.SyntaxTree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>().Single();
            var position = invocation.FullSpan.Start;


            SemanticModel speculativeSemanticModel;
            var statement = SyntaxFactory.ParseStatement("var foo = new A().M();");

            semanticModel.TryGetSpeculativeSemanticModel(position, statement, out speculativeSemanticModel);
            var creationExpression =
                speculativeSemanticModel.GetTypeInfo(
                    statement.DescendantNodes().OfType<ObjectCreationExpressionSyntax>().Single());

            Assert.Equal("A", creationExpression.Type.Name);
        }


        [Fact]
        public void TestAccessChecksInsideLambdaExpression()
        {
            var source = @"
using System.Collections.Generic;
 
class P { bool _p; }

class C
{
    static void M()
    {
        var tmp = new List<P>();
        tmp.Find(a => true);
    }
}
";

            var tree = SyntaxFactory.ParseSyntaxTree(source);
            var comp = CreateCompilationWithMscorlib(tree);
            var model = comp.GetSemanticModel(tree,suppressAccessChecks: true);

            var expr = (ExpressionSyntax)tree.GetCompilationUnitRoot().DescendantNodes().OfType<SimpleLambdaExpressionSyntax>().Single().Body;

            var symbolInfo = model.GetSpeculativeSymbolInfo(expr.FullSpan.Start, 
                                                           SyntaxFactory.ParseExpression("a._p"), 
                                                           SpeculativeBindingOption.BindAsExpression);

            Assert.Equal("_p", symbolInfo.Symbol.Name);
        }

        [Fact]
        public void TestExtensionMethodInInternalClass()
        {
            var compilationA = CreateCompilationWithMscorlibAndSystemCore(@"
public class A
{
    A M() { return new A(); }
    internal int _num;
}

internal static class E
{
    internal static int InternalExtension(this A theClass, int newNum)
    {
        theClass._num = newNum;
        
        return newNum;
    }
}
");

            var referenceA = MetadataReference.CreateFromStream(compilationA.EmitToStream());

            var compilationB = CreateCompilationWithMscorlib(@"
class B 
{
    void Main() 
    {
        new A().M();
    }
}

", new MetadataReference[] { referenceA }, TestOptions.ReleaseDll.WithMetadataImportOptions(MetadataImportOptions.All));

            var syntaxTree = compilationB.SyntaxTrees[0];
            var semanticModel = compilationB.GetSemanticModel(syntaxTree, suppressAccessChecks: true);

            var invocation = syntaxTree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>().Single();

            Assert.Equal("A", semanticModel.GetTypeInfo(invocation).Type.Name);
            Assert.Equal("M", semanticModel.GetSymbolInfo(invocation).Symbol.Name);

            var speculativeInvocation = SyntaxFactory.ParseExpression("new A().InternalExtension(67)");
            var position = invocation.FullSpan.Start;

            Assert.Equal("Int32", semanticModel.GetSpeculativeTypeInfo(position, speculativeInvocation, SpeculativeBindingOption.BindAsExpression).Type.Name);
            Assert.Equal("InternalExtension", semanticModel.GetSpeculativeSymbolInfo(position, speculativeInvocation, SpeculativeBindingOption.BindAsExpression).Symbol.Name);            
        }

        [Fact]
        public void TestGetSpeculativeSemanticModelForPropertyAccessorBody()
        {
            var compilation = CreateCompilationWithMscorlib(@"
class R
{
    private int _p;
}

class C : R 
{
    
    private int M
    {
        set
        {
            int y = 1000;
        }
    }
}
");

            var blockStatement = (BlockSyntax)SyntaxFactory.ParseStatement(@"
{ 
   int z = 0; 

   _p = 123L;
}
");

            var tree = compilation.SyntaxTrees[0];
            var root = tree.GetCompilationUnitRoot();
            var model = compilation.GetSemanticModel(tree, suppressAccessChecks: true);

            AccessorDeclarationSyntax accesorDecl = root.DescendantNodes().OfType<AccessorDeclarationSyntax>().Single();
            
            var speculatedMethod = accesorDecl.ReplaceNode(accesorDecl.Body, blockStatement);

            SemanticModel speculativeModel;
            var success =
                model.TryGetSpeculativeSemanticModelForMethodBody(
                    accesorDecl.Body.Statements[0].SpanStart, speculatedMethod, out speculativeModel);

            Assert.True(success);
            Assert.NotNull(speculativeModel);

            var p =
                speculativeModel.SyntaxTree.GetRoot()
                    .DescendantNodes()
                    .OfType<IdentifierNameSyntax>()
                    .Single(s => s.Identifier.ValueText == "_p");

            var symbolSpeculation =
                speculativeModel.GetSpeculativeSymbolInfo(p.FullSpan.Start, p, SpeculativeBindingOption.BindAsExpression);

            Assert.Equal("_p", symbolSpeculation.Symbol.Name);

            var typeSpeculation =
                speculativeModel.GetSpeculativeTypeInfo(p.FullSpan.Start, p, SpeculativeBindingOption.BindAsExpression);
            
            Assert.Equal("Int32", typeSpeculation.Type.Name);
        }
    }
}