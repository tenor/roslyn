﻿using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Language.StandardClassification;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;

namespace Roslyn.Editor.InteractiveWindow.Commands
{
    [Export(typeof(IClassifierProvider))]
    [ContentType(InteractiveContentTypeNames.InteractiveCommandContentType)]
    [TextViewRole(PredefinedTextViewRoles.Document)]
    internal sealed class CommandClassifierProvider : IClassifierProvider
    {
        [Import]
        public IStandardClassificationService ClassificationRegistry { get; set; }

        public IClassifier GetClassifier(ITextBuffer textBuffer)
        {
            var commands = textBuffer.GetInteractiveWindow().GetInteractiveCommands();
            if (commands != null)
            {
                return new CommandClassifier(ClassificationRegistry, commands);
            }

            return null;
        }
    }
}
