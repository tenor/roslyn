// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Composition;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Shared.TestHooks;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.Diagnostics
{
    [Export(typeof(IDiagnosticService)), Shared]
    internal class DiagnosticService : IDiagnosticService
    {
        private const string DiagnosticsUpdatedEventName = "DiagnosticsUpdated";

        private readonly IAsynchronousOperationListener listener;
        private readonly EventMap eventMap;
        private readonly SimpleTaskQueue eventQueue;
        private readonly ImmutableArray<IDiagnosticUpdateSource> updateSources;

        private readonly object gate;
        private readonly Dictionary<IDiagnosticUpdateSource, Dictionary<object, Data>> map;

        [ImportingConstructor]
        public DiagnosticService(
            [ImportMany] IEnumerable<IDiagnosticUpdateSource> diagnosticUpdateSource,
            [ImportMany] IEnumerable<Lazy<IAsynchronousOperationListener, FeatureMetadata>> asyncListeners)
        {
            // queue to serialize events.
            this.eventMap = new EventMap();
            this.eventQueue = new SimpleTaskQueue(TaskScheduler.Default);

            this.updateSources = diagnosticUpdateSource.AsImmutable();
            this.listener = new AggregateAsynchronousOperationListener(asyncListeners, FeatureAttribute.DiagnosticService);

            this.gate = new object();
            this.map = new Dictionary<IDiagnosticUpdateSource, Dictionary<object, Data>>();

            // connect each diagnostic update source to events
            ConnectDiagnosticsUpdatedEvents();
        }

        public event EventHandler<DiagnosticsUpdatedArgs> DiagnosticsUpdated
        {
            add
            {
                this.eventMap.AddEventHandler(DiagnosticsUpdatedEventName, value);
            }

            remove
            {
                this.eventMap.RemoveEventHandler(DiagnosticsUpdatedEventName, value);
            }
        }

        private void RaiseDiagnosticsUpdated(object sender, DiagnosticsUpdatedArgs args)
        {
            var handlers = this.eventMap.GetEventHandlers<EventHandler<DiagnosticsUpdatedArgs>>(DiagnosticsUpdatedEventName);
            if (handlers.Length > 0)
            {
                var eventToken = this.listener.BeginAsyncOperation(DiagnosticsUpdatedEventName);
                this.eventQueue.ScheduleTask(() =>
                {
                    UpdateDataMap(sender, args);

                    foreach (var handler in handlers)
                    {
                        handler(sender, args);
                    }
                }).CompletesAsyncOperation(eventToken);
            }
        }

        private void UpdateDataMap(object sender, DiagnosticsUpdatedArgs args)
        {
            var updateSource = sender as IDiagnosticUpdateSource;
            if (updateSource == null || updateSource.SupportGetDiagnostics)
            {
                return;
            }

            Contract.Requires(this.updateSources.IndexOf(updateSource) >= 0);

            // we expect someone who uses this ability to small.
            lock (this.gate)
            {
                var list = this.map.GetOrAdd(updateSource, _ => new Dictionary<object, Data>());
                var data = new Data(args);

                list.Remove(data.Id);
                if (list.Count == 0 && args.Diagnostics.Length == 0)
                {
                    this.map.Remove(updateSource);
                    return;
                }

                list.Add(args.Id, data);
            }
        }

        private void ConnectDiagnosticsUpdatedEvents()
        {
            foreach (var source in this.updateSources)
            {
                source.DiagnosticsUpdated += OnDiagnosticsUpdated;
            }
        }

        private void OnDiagnosticsUpdated(object sender, DiagnosticsUpdatedArgs e)
        {
            RaiseDiagnosticsUpdated(sender, e);
        }

        public IEnumerable<DiagnosticData> GetDiagnostics(
            Workspace workspace, ProjectId projectId, DocumentId documentId, object id, CancellationToken cancellationToken)
        {
            if (id != null)
            {
                // get specific one
                return GetSpecificDiagnostics(workspace, projectId, documentId, id, cancellationToken);
            }

            // get aggregated ones
            return GetDiagnostics(workspace, projectId, documentId, cancellationToken);
        }

        private IEnumerable<DiagnosticData> GetSpecificDiagnostics(Workspace workspace, ProjectId projectId, DocumentId documentId, object id, CancellationToken cancellationToken)
        {
            foreach (var source in this.updateSources)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (source.SupportGetDiagnostics)
                {
                    var diagnostics = source.GetDiagnostics(workspace, projectId, documentId, id, cancellationToken);
                    if (diagnostics != null && diagnostics.Length > 0)
                    {
                        return diagnostics;
                    }
                }
                else
                {
                    using (var pool = SharedPools.Default<List<Data>>().GetPooledObject())
                    {
                        AppendMatchingData(source, workspace, projectId, documentId, id, pool.Object);
                        Contract.Requires(pool.Object.Count == 0 || pool.Object.Count == 1);

                        if (pool.Object.Count == 1)
                        {
                            return pool.Object[0].Diagnostics;
                        }
                    }
                }
            }

            return SpecializedCollections.EmptyEnumerable<DiagnosticData>();
        }

        private IEnumerable<DiagnosticData> GetDiagnostics(
            Workspace workspace, ProjectId projectId, DocumentId documentId, CancellationToken cancellationToken)
        {
            foreach (var source in this.updateSources)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (source.SupportGetDiagnostics)
                {
                    foreach (var diagnostic in source.GetDiagnostics(workspace, projectId, documentId, null, cancellationToken))
                    {
                        yield return diagnostic;
                    }
                }
                else
                {
                    using (var list = SharedPools.Default<List<Data>>().GetPooledObject())
                    {
                        AppendMatchingData(source, workspace, projectId, documentId, null, list.Object);

                        foreach (var data in list.Object)
                        {
                            foreach (var diagnostic in data.Diagnostics)
                            {
                                yield return diagnostic;
                            }
                        }
                    }
                }
            }
        }

        private void AppendMatchingData(
            IDiagnosticUpdateSource source, Workspace workspace, ProjectId projectId, DocumentId documentId, object id, List<Data> list)
        {
            lock (this.gate)
            {
                Dictionary<object, Data> current;
                if (!this.map.TryGetValue(source, out current))
                {
                    return;
                }

                if (id != null)
                {
                    Data data;
                    if (current.TryGetValue(id, out data))
                    {
                        list.Add(data);
                    }

                    return;
                }

                foreach (var data in current.Values)
                {
                    if (TryAddData(documentId, data, d => d.DocumentId, list) ||
                        TryAddData(projectId, data, d => d.ProjectId, list) ||
                        TryAddData(workspace, data, d => d.Workspace, list))
                    {
                        continue;
                    }
                }
            }
        }

        private bool TryAddData<T>(T key, Data data, Func<Data, T> keyGetter, List<Data> result) where T : class
        {
            if (key == null)
            {
                return false;
            }

            if (key == keyGetter(data))
            {
                result.Add(data);
            }

            return true;
        }

        public ImmutableDictionary<object, ImmutableArray<DiagnosticData>> GetEngineCachedDiagnostics(DocumentId documentId)
        {
            lock (this.gate)
            {
                var builder = ImmutableDictionary.CreateBuilder<object, ImmutableArray<DiagnosticData>>();
                foreach (var diagnosticMap in this.map.Values)
                {
                    foreach (var kv in diagnosticMap)
                    {
                        var key = kv.Key;
                        var data = kv.Value;

                        if (documentId != data.DocumentId || data.Diagnostics.Length == 0)
                        {
                            continue;
                        }

                        builder.Add(key, data.Diagnostics);
                    }
                }

                return builder.ToImmutable();
            }
        }

        private struct Data : IEquatable<Data>
        {
            public readonly Workspace Workspace;
            public readonly ProjectId ProjectId;
            public readonly DocumentId DocumentId;
            public readonly object Id;
            public readonly ImmutableArray<DiagnosticData> Diagnostics;

            public Data(DiagnosticsUpdatedArgs args)
            {
                this.Workspace = args.Workspace;
                this.ProjectId = args.ProjectId;
                this.DocumentId = args.DocumentId;
                this.Id = args.Id;
                this.Diagnostics = args.Diagnostics;
            }

            public bool Equals(Data other)
            {
                return this.Workspace == other.Workspace &&
                       this.ProjectId == other.ProjectId &&
                       this.DocumentId == other.DocumentId &&
                       this.Id == other.Id;
            }

            public override bool Equals(object obj)
            {
                return (obj is Data) && Equals((Data)obj);
            }

            public override int GetHashCode()
            {
                return Hash.Combine(Workspace,
                       Hash.Combine(ProjectId,
                       Hash.Combine(DocumentId,
                       Hash.Combine(Id, 1))));
            }
        }
    }
}
