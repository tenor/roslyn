// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Host;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Microsoft.CodeAnalysis.SolutionCrawler.State;
using Microsoft.CodeAnalysis.Text;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.Diagnostics
{
    internal partial class DiagnosticAnalyzerService
    {
        internal partial class DiagnosticIncrementalAnalyzer
        {
            internal class DiagnosticState : AbstractAnalyzerState<object, object, AnalysisData>
            {
                private const int FormatVersion = 5;

                private readonly string stateName;
                private readonly VersionStamp version;
                private readonly string language;

                public DiagnosticState(string stateName, VersionStamp version, string language)
                {
                    Contract.ThrowIfNull(stateName);

                    this.stateName = stateName;
                    this.version = version;
                    this.language = language;
                }

                internal string Name
                {
                    get { return stateName; }
                }

                internal string Language
                {
                    get { return language; }
                }

                protected override object GetCacheKey(object value)
                {
                    var document = value as Document;
                    if (document != null)
                    {
                        return document.Id;
                    }

                    var project = (Project)value;
                    return project.Id;
                }

                protected override Solution GetSolution(object value)
                {
                    var document = value as Document;
                    if (document != null)
                    {
                        return document.Project.Solution;
                    }

                    var project = (Project)value;
                    return project.Solution;
                }

                protected override bool ShouldCache(object value)
                {
                    var document = value as Document;
                    if (document != null)
                    {
                        return document.IsOpen();
                    }

                    var project = (Project)value;
                    return project.Solution.Workspace.GetOpenDocumentIds(project.Id).Any();
                }

                protected override Task<Stream> ReadStreamAsync(IPersistentStorage storage, object value, CancellationToken cancellationToken)
                {
                    var document = value as Document;
                    if (document != null)
                    {
                        return storage.ReadStreamAsync(document, stateName, cancellationToken);
                    }

                    var project = (Project)value;
                    return storage.ReadStreamAsync(project, stateName, cancellationToken);
                }

                protected override AnalysisData TryGetExistingData(Stream stream, object value, CancellationToken cancellationToken)
                {
                    var document = value as Document;
                    if (document != null)
                    {
                        return TryGetExistingData(stream, document.Project, document, cancellationToken);
                    }

                    var project = (Project)value;
                    return TryGetExistingData(stream, project, null, cancellationToken);
                }

                private AnalysisData TryGetExistingData(Stream stream, Project project, Document document, CancellationToken cancellationToken)
                {
                    var list = SharedPools.Default<List<DiagnosticData>>().AllocateAndClear();
                    try
                    {
                        using (var reader = new ObjectReader(stream))
                        {
                            var format = reader.ReadInt32();
                            if (format != FormatVersion)
                            {
                                return null;
                            }

                            // saved data is for same provider of different version of dll
                            var providerVersion = VersionStamp.ReadFrom(reader);
                            if (providerVersion != this.version)
                            {
                                return null;
                            }

                            var textVersion = VersionStamp.ReadFrom(reader);
                            var dataVersion = VersionStamp.ReadFrom(reader);
                            if (textVersion == VersionStamp.Default || dataVersion == VersionStamp.Default)
                            {
                                return null;
                            }

                            AppendItems(reader, project, document, list, cancellationToken);

                            return new AnalysisData(textVersion, dataVersion, list.ToImmutableArray<DiagnosticData>());
                        }
                    }
                    catch (Exception)
                    {
                        return null;
                    }
                    finally
                    {
                        SharedPools.Default<List<DiagnosticData>>().ClearAndFree(list);
                    }
                }

                private void AppendItems(ObjectReader reader, Project project, Document document, List<DiagnosticData> list, CancellationToken cancellationToken)
                {
                    var count = reader.ReadInt32();

                    for (var i = 0; i < count; i++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        var id = reader.ReadString();
                        var category = reader.ReadString();

                        var message = reader.ReadString();
                        var messageFormat = reader.ReadString();
                        var title = reader.ReadString();
                        var description = reader.ReadString();
                        var helpLink = reader.ReadString();
                        var severity = (DiagnosticSeverity)reader.ReadInt32();
                        var defaultSeverity = (DiagnosticSeverity)reader.ReadInt32();
                        var isEnabledByDefault = reader.ReadBoolean();
                        var warningLevel = reader.ReadInt32();

                        var start = reader.ReadInt32();
                        var length = reader.ReadInt32();
                        var textSpan = new TextSpan(start, length);

                        var originalFile = reader.ReadString();
                        var originalStartLine = reader.ReadInt32();
                        var originalStartColumn = reader.ReadInt32();
                        var originalEndLine = reader.ReadInt32();
                        var originalEndColumn = reader.ReadInt32();

                        var mappedFile = reader.ReadString();
                        var mappedStartLine = reader.ReadInt32();
                        var mappedStartColumn = reader.ReadInt32();
                        var mappedEndLine = reader.ReadInt32();
                        var mappedEndColumn = reader.ReadInt32();

                        var customTagsCount = reader.ReadInt32();
                        var customTags = GetCustomTags(reader, customTagsCount);

                        list.Add(new DiagnosticData(
                            id, category, message, messageFormat, severity, defaultSeverity, isEnabledByDefault, warningLevel, customTags,
                            project.Solution.Workspace, project.Id, document != null ? document.Id : null, document != null ? textSpan : (TextSpan?)null,
                            mappedFile, mappedStartLine, mappedStartColumn, mappedEndLine, mappedEndColumn,
                            originalFile, originalStartLine, originalStartColumn, originalEndLine, originalEndColumn,
                            title: title,
                            description: description,
                            helpLink: helpLink));
                    }
                }

                private static IReadOnlyList<string> GetCustomTags(ObjectReader reader, int count)
                {
                    if (count > 0)
                    {
                        var tags = new List<string>(count);
                        for (var i = 0; i < count; i++)
                        {
                            tags.Add(reader.ReadString());
                        }

                        return new ReadOnlyCollection<string>(tags);
                    }

                    return SpecializedCollections.EmptyReadOnlyList<string>();
                }

                protected override Task<bool> WriteStreamAsync(IPersistentStorage storage, object value, Stream stream, CancellationToken cancellationToken)
                {
                    var document = value as Document;
                    if (document != null)
                    {
                        return storage.WriteStreamAsync(document, stateName, stream, cancellationToken);
                    }

                    var project = (Project)value;
                    return storage.WriteStreamAsync(project, stateName, stream, cancellationToken);
                }

                protected override void WriteTo(Stream stream, AnalysisData data, CancellationToken cancellationToken)
                {
                    using (var writer = new ObjectWriter(stream, cancellationToken: cancellationToken))
                    {
                        writer.WriteInt32(FormatVersion);
                        this.version.WriteTo(writer);
                        data.TextVersion.WriteTo(writer);
                        data.DataVersion.WriteTo(writer);

                        writer.WriteInt32(data.Items.Length);

                        foreach (var item in data.Items)
                        {
                            cancellationToken.ThrowIfCancellationRequested();

                            writer.WriteString(item.Id);
                            writer.WriteString(item.Category);

                            writer.WriteString(item.Message);
                            writer.WriteString(item.MessageFormat);
                            writer.WriteString(item.Title);
                            writer.WriteString(item.Description);
                            writer.WriteString(item.HelpLink);
                            writer.WriteInt32((int)item.Severity);
                            writer.WriteInt32((int)item.DefaultSeverity);
                            writer.WriteBoolean(item.IsEnabledByDefault);
                            writer.WriteInt32(item.WarningLevel);

                            if (item.HasTextSpan)
                            {
                                // document state
                                writer.WriteInt32(item.TextSpan.Start);
                                writer.WriteInt32(item.TextSpan.Length);
                            }
                            else
                            {
                                // project state
                                writer.WriteInt32(0);
                                writer.WriteInt32(0);
                            }

                            writer.WriteString(item.OriginalFilePath);
                            writer.WriteInt32(item.OriginalStartLine);
                            writer.WriteInt32(item.OriginalStartColumn);
                            writer.WriteInt32(item.OriginalEndLine);
                            writer.WriteInt32(item.OriginalEndColumn);

                            writer.WriteString(item.MappedFilePath);
                            writer.WriteInt32(item.MappedStartLine);
                            writer.WriteInt32(item.MappedStartColumn);
                            writer.WriteInt32(item.MappedEndLine);
                            writer.WriteInt32(item.MappedEndColumn);

                            writer.WriteInt32(item.CustomTags.Count);
                            foreach (var tag in item.CustomTags)
                            {
                                writer.WriteString(tag);
                            }
                        }
                    }
                }
            }
        }
    }
}
