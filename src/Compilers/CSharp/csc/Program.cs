﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

namespace Microsoft.CodeAnalysis.CSharp.CommandLine
{
    class Program
    {
        static int Main(string[] args)
        {
            return Csc.Run(args);
        }
    }
}
