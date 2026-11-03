using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using Blasphemous.ModdingAPI;

namespace Blasphemous.Multiplayer.Client.Network
{
    /// <summary>
    /// Keeps normal local diagnostics and forwards a bounded stream to the server-assigned
    /// session collector. The server timestamps batches on its monotonic session clock.
    /// </summary>
    internal static class SharedDiagnosticLogger
    {
        private const int MaxPendingEntries = 512;
        private const int MaxBatchEntries = 2;
        private const int MaxMessageLength = 900;

        private static readonly object Sync = new object();
        private static readonly Queue<Entry> Pending = new Queue<Entry>();
        private static readonly Stopwatch LocalClock = new Stopwatch();
        private static long _nextSequence;
        private static string _sessionId;
        private static bool _isCollector;
        private static int _droppedEntries;

        internal static void Info(string message)
        {
            ModLog.Info(message);
            Enqueue(message);
        }

        internal static void Warn(string message)
        {
            ModLog.Warn(message);
            Enqueue(message);
        }

        private static void Enqueue(string message)
        {
            if (string.IsNullOrEmpty(message))
                return;

            lock (Sync)
            {
                if (string.IsNullOrEmpty(_sessionId))
                    return;

                if (message.Length > MaxMessageLength)
                    message = message.Substring(0, MaxMessageLength);

                if (Pending.Count >= MaxPendingEntries)
                {
                    Pending.Dequeue();
                    _droppedEntries++;
                }

                Pending.Enqueue(new Entry(++_nextSequence, LocalClock.ElapsedMilliseconds, message));
            }
        }

        internal static byte[] TakeBatch()
        {
            lock (Sync)
            {
                if (string.IsNullOrEmpty(_sessionId) || Pending.Count == 0)
                    return null;

                bool hasDropNotice = _droppedEntries > 0;
                int count = Math.Min(MaxBatchEntries - (hasDropNotice ? 1 : 0), Pending.Count) + (hasDropNotice ? 1 : 0);
                if (count <= 0)
                    return null;

                using (MemoryStream stream = new MemoryStream())
                using (BinaryWriter writer = new BinaryWriter(stream, Encoding.UTF8))
                {
                    writer.Write((ushort)count);
                    if (hasDropNotice)
                    {
                        writer.Write(0L);
                        writer.Write(LocalClock.ElapsedMilliseconds);
                        writer.Write($"[SharedDiag] Local queue dropped {_droppedEntries} oldest diagnostic entries");
                        _droppedEntries = 0;
                    }

                    int queuedCount = count - (hasDropNotice ? 1 : 0);
                    for (int i = 0; i < queuedCount; i++)
                    {
                        Entry entry = Pending.Dequeue();
                        writer.Write(entry.Sequence);
                        writer.Write(entry.LocalElapsedMilliseconds);
                        writer.Write(entry.Message);
                    }
                    writer.Flush();
                    return stream.ToArray();
                }
            }
        }

        internal static void SetSession(string sessionId, string collectorName, bool isCollector, long serverElapsedMilliseconds)
        {
            lock (Sync)
            {
                bool changed = _sessionId != sessionId;
                _sessionId = sessionId;
                _isCollector = isCollector;
                if (changed)
                {
                    Pending.Clear();
                    _nextSequence = 0;
                    _droppedEntries = 0;
                    LocalClock.Reset();
                    LocalClock.Start();
                }
            }

            ModLog.Info($"[SharedDiag] Session assigned session={sessionId}, collector={collectorName}, localIsCollector={isCollector}, serverElapsedMs={serverElapsedMilliseconds}");
        }

        internal static void ResetSession()
        {
            lock (Sync)
            {
                _sessionId = null;
                _isCollector = false;
                Pending.Clear();
                _nextSequence = 0;
                _droppedEntries = 0;
                LocalClock.Reset();
            }
        }

        internal static void ReceiveMergedBatch(byte[] data)
        {
            if (data == null || data.Length == 0)
                return;

            try
            {
                using (MemoryStream stream = new MemoryStream(data))
                using (BinaryReader reader = new BinaryReader(stream, Encoding.UTF8))
                {
                    string sessionId = reader.ReadString();
                    long serverElapsedMilliseconds = reader.ReadInt64();
                    long relaySequence = reader.ReadInt64();
                    string sourceName = reader.ReadString();
                    int batchLength = reader.ReadInt32();
                    if (batchLength <= 0 || batchLength > 8192 || batchLength != stream.Length - stream.Position)
                        return;

                    byte[] batch = reader.ReadBytes(batchLength);
                    string collectorReceivedUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
                    lock (Sync)
                    {
                        if (!_isCollector || string.IsNullOrEmpty(_sessionId) || _sessionId != sessionId)
                            return;
                    }

                    using (MemoryStream batchStream = new MemoryStream(batch))
                    using (BinaryReader batchReader = new BinaryReader(batchStream, Encoding.UTF8))
                    {
                        ushort count = batchReader.ReadUInt16();
                        if (count > MaxBatchEntries)
                            return;

                        for (int i = 0; i < count; i++)
                        {
                            long sourceSequence = batchReader.ReadInt64();
                            long sourceElapsedMilliseconds = batchReader.ReadInt64();
                            string message = batchReader.ReadString();
                            if (message.Length > MaxMessageLength)
                                message = message.Substring(0, MaxMessageLength);

                            ModLog.Info($"[SharedDiagMerged] session={sessionId} serverMs={serverElapsedMilliseconds} relaySeq={relaySequence} collectorReceivedUtc={collectorReceivedUtc} source={sourceName} sourceSeq={sourceSequence} sourceMs={sourceElapsedMilliseconds} {message}");
                        }

                        if (batchStream.Position != batchStream.Length)
                            ModLog.Warn("[SharedDiag] Ignored merged diagnostic batch with trailing data");
                    }
                }
            }
            catch (Exception ex)
            {
                ModLog.Warn($"[SharedDiag] Ignored malformed merged diagnostic batch: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private readonly struct Entry
        {
            internal readonly long Sequence;
            internal readonly long LocalElapsedMilliseconds;
            internal readonly string Message;

            internal Entry(long sequence, long localElapsedMilliseconds, string message)
            {
                Sequence = sequence;
                LocalElapsedMilliseconds = localElapsedMilliseconds;
                Message = message;
            }
        }
    }
}