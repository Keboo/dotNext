﻿using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace DotNext.Net.Cluster.Consensus.Raft
{
    using IO;

    public partial class PersistentState
    {
        internal readonly struct LogEntryMetadata
        {
            internal readonly long Term, Timestamp, Length, Offset;

            private LogEntryMetadata(DateTimeOffset timeStamp, long term, long offset, long length)
            {
                Term = term;
                Timestamp = timeStamp.UtcTicks;
                Length = length;
                Offset = offset;
            }

            internal static LogEntryMetadata Create<TLogEntry>(TLogEntry entry, long offset, long length)
                where TLogEntry : IRaftLogEntry
                => new LogEntryMetadata(entry.Timestamp, entry.Term, offset, length);

            internal static int Size => Unsafe.SizeOf<LogEntryMetadata>();
        }

        internal readonly struct SnapshotMetadata
        {
            internal readonly long Index;
            internal readonly LogEntryMetadata RecordMetadata;

            private SnapshotMetadata(LogEntryMetadata metadata, long index)
            {
                Index = index;
                RecordMetadata = metadata;
            }

            internal static SnapshotMetadata Create<TLogEntry>(TLogEntry snapshot, long index, long length)
                where TLogEntry : IRaftLogEntry
                => new SnapshotMetadata(LogEntryMetadata.Create(snapshot, Size, length), index);

            internal static int Size => Unsafe.SizeOf<SnapshotMetadata>();
        }

        private abstract class ConcurrentStorageAccess : Disposable, IAsyncDisposable, IFlushable
        {
            // do not derive from FileStream because some virtual methods
            // assumes that they are overridden and do async calls inefficiently
            private protected readonly FileStream fs;
            private readonly StreamSegment[] readers;   // a pool of read-only streams that can be shared between multiple readers in parallel

            [SuppressMessage("Reliability", "CA2000", Justification = "All streams are disposed in Dispose method")]
            private protected ConcurrentStorageAccess(string fileName, int bufferSize, int readersCount, FileOptions options)
            {
                fs = new FileStream(fileName, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, bufferSize, options);
                readers = new StreamSegment[readersCount];
                if (readersCount == 1)
                {
                    readers[0] = new StreamSegment(fs, true);
                }
                else
                {
                    foreach (ref var reader in readers.AsSpan())
                        reader = new StreamSegment(new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize, FileOptions.Asynchronous | FileOptions.RandomAccess), false);
                }
            }

            internal long Length => fs.Length;

            public Task FlushAsync(CancellationToken token = default) => fs.FlushAsync(token);

            public void Flush() => fs.Flush(true);

            internal string FileName => fs.Name;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private protected StreamSegment GetReadSessionStream(in DataAccessSession session) => readers[session.SessionId];

            internal Task FlushAsync(in DataAccessSession session, CancellationToken token)
                => GetReadSessionStream(session).FlushAsync(token);

            internal abstract void PopulateCache(in DataAccessSession session);

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    foreach (ref var reader in readers.AsSpan())
                    {
                        reader?.Dispose();
                        reader = null;
                    }

                    fs.Dispose();
                }

                base.Dispose(disposing);
            }

            public async ValueTask DisposeAsync()
            {
                foreach (Stream? reader in readers)
                {
                    if (reader is not null)
                        await reader.DisposeAsync().ConfigureAwait(false);
                }

                Array.Clear(readers, 0, readers.Length);
                await fs.DisposeAsync().ConfigureAwait(false);
                base.Dispose(true);
            }
        }
    }
}
