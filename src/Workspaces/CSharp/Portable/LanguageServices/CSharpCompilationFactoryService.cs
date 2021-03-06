﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Composition;
using Microsoft.CodeAnalysis.Host;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.LanguageServices;

namespace Microsoft.CodeAnalysis.CSharp
{
    [ExportLanguageService(typeof(ICompilationFactoryService), LanguageNames.CSharp), Shared]
    internal class CSharpCompilationFactoryService : ICompilationFactoryService
    {
        private static readonly CSharpCompilationOptions defaultOptions = new CSharpCompilationOptions(OutputKind.ConsoleApplication, concurrentBuild: false);

        Compilation ICompilationFactoryService.CreateCompilation(string assemblyName, CompilationOptions options)
        {
            return CSharpCompilation.Create(
                assemblyName, 
                options: (CSharpCompilationOptions)options ?? defaultOptions);
        }

        Compilation ICompilationFactoryService.CreateSubmissionCompilation(string assemblyName, CompilationOptions options, Type hostObjectType)
        {
            return CSharpCompilation.CreateSubmission(
                assemblyName,
                options: (CSharpCompilationOptions)options,
                previousSubmission: null,
                hostObjectType: hostObjectType);
        }

        Compilation ICompilationFactoryService.GetCompilationFromCompilationReference(MetadataReference reference)
        {
            var compilationRef = reference as CompilationReference;
            return (compilationRef != null) ? compilationRef.Compilation : null;
        }

        bool ICompilationFactoryService.IsCompilationReference(MetadataReference reference)
        {
            return reference is CompilationReference;
        }

        CompilationOptions ICompilationFactoryService.GetDefaultCompilationOptions()
        {
            return defaultOptions;
        }
    }
}
