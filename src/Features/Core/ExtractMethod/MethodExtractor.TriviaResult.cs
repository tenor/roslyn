// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.ExtractMethod
{
    internal abstract partial class MethodExtractor
    {
        protected abstract class TriviaResult
        {
            private readonly int endOfLineKind;
            private readonly int whitespaceKind;

            private readonly ITriviaSavedResult result;

            public TriviaResult(SemanticDocument document, ITriviaSavedResult result, int endOfLineKind, int whitespaceKind)
            {
                this.SemanticDocument = document;

                this.result = result;
                this.endOfLineKind = endOfLineKind;
                this.whitespaceKind = whitespaceKind;
            }

            protected abstract AnnotationResolver GetAnnotationResolver(SyntaxNode callsite, SyntaxNode methodDefinition);
            protected abstract TriviaResolver GetTriviaResolver(SyntaxNode methodDefinition);

            public SemanticDocument SemanticDocument { get; private set; }

            public async Task<OperationStatus<SemanticDocument>> ApplyAsync(GeneratedCode generatedCode, CancellationToken cancellationToken)
            {
                var document = generatedCode.SemanticDocument;
                var root = document.Root;

                var callsiteAnnotation = generatedCode.CallSiteAnnotation;
                var methodDefinitionAnnotation = generatedCode.MethodDefinitionAnnotation;

                var callsite = root.GetAnnotatedNodesAndTokens(callsiteAnnotation).SingleOrDefault().AsNode();
                var method = root.GetAnnotatedNodesAndTokens(methodDefinitionAnnotation).SingleOrDefault().AsNode();

                var annotationResolver = GetAnnotationResolver(callsite, method);
                var triviaResolver = GetTriviaResolver(method);
                if (annotationResolver == null || triviaResolver == null)
                {
                    // bug # 6644
                    // this could happen in malformed code. return as it was.
                    var status = new OperationStatus(OperationStatusFlag.None, FeaturesResources.CantNotConstructFinalTree);
                    return status.With(document);
                }

                return OperationStatus.Succeeded.With(
                    await document.WithSyntaxRootAsync(this.result.RestoreTrivia(root, annotationResolver, triviaResolver), cancellationToken).ConfigureAwait(false));
            }

            protected IEnumerable<SyntaxTrivia> FilterTriviaList(IEnumerable<SyntaxTrivia> list)
            {
                // has noisy token
                if (list.Any(t => t.RawKind != endOfLineKind && t.RawKind != whitespaceKind))
                {
                    return RemoveLeadingElasticBeforeEndOfLine(list);
                }

                // whitespace only
                return MergeLineBreaks(list);
            }

            protected IEnumerable<SyntaxTrivia> RemoveBlankLines(IEnumerable<SyntaxTrivia> list)
            {
                // remove any blank line at the beginging
                var currentLine = new List<SyntaxTrivia>();
                var result = new List<SyntaxTrivia>();

                var seenFirstEndOfLine = false;
                int i = 0;

                foreach (var trivia in list)
                {
                    i++;

                    if (trivia.RawKind == endOfLineKind)
                    {
                        if (seenFirstEndOfLine)
                        {
                            // empty line. remove it
                            if (currentLine.All(t => t.RawKind == endOfLineKind || t.RawKind == whitespaceKind))
                            {
                                continue;
                            }

                            // non empty line after the first end of line.
                            // return now
                            return result.Concat(currentLine).Concat(list.Skip(i - 1));
                        }
                        else
                        {
                            seenFirstEndOfLine = true;

                            result.AddRange(currentLine);
                            result.Add(trivia);
                            currentLine.Clear();

                            continue;
                        }
                    }

                    currentLine.Add(trivia);
                }

                return result.Concat(currentLine);
            }

            protected IEnumerable<SyntaxTrivia> RemoveLeadingElasticBeforeEndOfLine(IEnumerable<SyntaxTrivia> list)
            {
                var trivia = list.FirstOrDefault();
                if (!trivia.IsElastic())
                {
                    return list;
                }

                var listWithoutHead = list.Skip(1);
                trivia = listWithoutHead.FirstOrDefault();
                if (trivia.RawKind == endOfLineKind)
                {
                    return listWithoutHead;
                }

                if (trivia.IsElastic())
                {
                    return RemoveLeadingElasticBeforeEndOfLine(listWithoutHead);
                }

                return list;
            }

            protected IEnumerable<SyntaxTrivia> MergeLineBreaks(IEnumerable<SyntaxTrivia> list)
            {
                // this will make sure that it doesn't have more than two subsequent end of line
                // trivia without any noisy trivia
                var stack = new Stack<SyntaxTrivia>();
                int numberOfEndOfLinesWithoutAnyNoisyTrivia = 0;

                foreach (var trivia in list)
                {
                    if (trivia.IsElastic())
                    {
                        stack.Push(trivia);
                        continue;
                    }

                    if (trivia.RawKind == endOfLineKind)
                    {
                        numberOfEndOfLinesWithoutAnyNoisyTrivia++;

                        if (numberOfEndOfLinesWithoutAnyNoisyTrivia > 2)
                        {
                            // get rid of any whitespace trivia from stack
                            var top = stack.Peek();
                            while (!top.IsElastic() && top.RawKind == whitespaceKind)
                            {
                                stack.Pop();
                                top = stack.Peek();
                            }

                            continue;
                        }
                    }

                    stack.Push(trivia);
                }

                return stack.Reverse();
            }
        }
    }
}
