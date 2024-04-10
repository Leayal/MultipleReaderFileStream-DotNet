using System;
using System.Collections.Concurrent;
using System.IO.MemoryMappedFiles;
using System.IO;
using System.Threading;
using System.Diagnostics.CodeAnalysis;
#if NET6_0_OR_GREATER
using System.Runtime.CompilerServices;
#endif

namespace Leayal.MultipleReaderFileStream
{
    /// <summary>A stream manager that is optimized for multiple-readers-single-writer situations.</summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>When the stream is written, it will halt all associated readers until the associated writer stream is closed.</item>
    /// <item>The class is thread-safe, however, the reader and writer streams are not.</item>
    /// </list>
    /// </remarks>
    public class OptimizedMultipleReaderFileStream
    {
        private static readonly ConcurrentDictionary<string, FileStreamBroker> CachedBrokers =
#if NET5_0_OR_GREATER
            new ConcurrentDictionary<string, FileStreamBroker>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
#else
            new ConcurrentDictionary<string, FileStreamBroker>(Environment.OSVersion.Platform == PlatformID.Win32NT ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
#endif

        /// <summary>Forces closing all existing stream brokers at the time this function is called.</summary>
        /// <remarks>This is an unsafe operation. You should call this when your application exits or when you absolutely fully understand why you need it.</remarks>
        public static void CloseAllHandles()
        {
            var count = CachedBrokers.Values.Count; // Do not abuse this property, it will walkthrough the whole dictionary per call to count.
            if (count == 0) return;
            var brokers = new FileStreamBroker[count];
            CachedBrokers.Values.CopyTo(brokers, 0);

            CachedBrokers.Clear();

            for (int i = 0; i < count; i++)
            {
                brokers[i].Dispose();
            }
        }


        /// <summary>Initializes a new instance by either opening or creating the file from given path.</summary>
        /// <param name="filepath">The file path to the file you want to open or create.</param>
        /// <returns>A broker's interface that allows you to open streams for reading and writing.</returns>
        public static IFileStreamBroker OpenOrCreateFile(string filepath)
        {
            return CachedBrokers.AddOrUpdate(filepath, path => new FileStreamBroker(path), (path, old) =>
            {
                if (old.IsValid) return old;
                old.Dispose();
                return new FileStreamBroker(path);
            });
        }

        sealed class FileStreamBroker : IFileStreamBroker
        {
            private FileStream? fs;
#if NET6_0_OR_GREATER
            private readonly object locker;
#else
            private object locker;
#endif
            private bool _created, _mapped;
            private MemoryMappedFile? memMappedFile;
            private readonly string filepath;
            // This is "expensive", as the execution may have to wait due to locks. Expensive cost here is the time execution length prolonged due to waitings, not memory.
            private readonly ReaderWriterLockSlim ioLocker;

            public FileStreamBroker(string filepath)
            {
                this.filepath = filepath;
                this.locker = new object();
                this.ioLocker = new ReaderWriterLockSlim(LockRecursionPolicy.SupportsRecursion);
            }

            public bool IsValid
            {
                get
                {
                    if (!this._created) return false;

                    if (this.fs is FileStream writeStream)
                    {
                        try
                        {
                            return !writeStream.SafeFileHandle.IsInvalid;
                        }
                        catch
                        {
                            return false;
                        }
                    }
                    return false;
                }
            }

            private FileStream FactoryFS() => new FileStream(this.filepath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, 4096, true);

            private MemoryMappedFile FactoryMap()
            {
#if NET8_0_OR_GREATER
                ArgumentNullException.ThrowIfNull(this.fs);
#else
                if (this.fs == null) throw new ArgumentNullException();
#endif
                return MemoryMappedFile.CreateFromFile(this.fs, null, 0, MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, true);
            }

            private void OpenOrCreate(out FileStream writeStream)
            {
#pragma warning disable CS8601 // Possible null reference assignment.
#if NET6_0_OR_GREATER
                writeStream = LazyInitializer.EnsureInitialized<FileStream>(ref this.fs, ref this._created, ref Unsafe.AsRef(in this.locker), this.FactoryFS);
#else
                writeStream = LazyInitializer.EnsureInitialized<FileStream>(ref this.fs, ref this._created, ref this.locker, this.FactoryFS);
#endif
#pragma warning restore CS8601 // Possible null reference assignment.
            }

            private bool TryGetMap([NotNullWhen(true)] out MemoryMappedFile? map)
            {
                if (this.fs == null || this.fs.Length == 0)
                {
                    map = null;
                    return false;
                }
#pragma warning disable CS8601 // Possible null reference assignment.
#if NET6_0_OR_GREATER
                map = LazyInitializer.EnsureInitialized<MemoryMappedFile>(ref this.memMappedFile, ref this._mapped, ref Unsafe.AsRef(in this.locker), this.FactoryMap);
#else
                map = LazyInitializer.EnsureInitialized<MemoryMappedFile>(ref this.memMappedFile, ref this._mapped, ref this.locker, this.FactoryMap);
#endif
#pragma warning restore CS8601 // Possible null reference assignment.
                try
                {
                    var handle = map.SafeMemoryMappedFileHandle;
                    if (handle.IsInvalid || handle.IsClosed)
                    {
                        map = RefreshMap();
                    }
                }
                catch
                {
                    map = RefreshMap();
                }
                return true;
            }

            private MemoryMappedFile RefreshMap()
            {
                if (this.fs == null || this.fs.Length == 0) throw new InvalidOperationException();

                var mappedFile = MemoryMappedFile.CreateFromFile(this.fs, null, 0, MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, true);
                Interlocked.Exchange(ref this.memMappedFile, mappedFile)?.Dispose();
                return mappedFile;
            }

            public void Dispose()
            {
                this.fs?.Dispose();
                this.memMappedFile?.Dispose();
            }

            public Stream OpenRead()
            {
                this.OpenOrCreate(out _);
                if (TryGetMap(out var memMapped))
                {
                    this.ioLocker.EnterReadLock();
                    var syncContext = SynchronizationContext.Current;
                    if (syncContext == null)
                    {
                        syncContext = new SynchronizationContext();
                        SynchronizationContext.SetSynchronizationContext(syncContext);
                    }
                    var readStream = memMapped.CreateViewStream(0, 0, MemoryMappedFileAccess.Read);
                    return new WrapperReadStream(readStream, this.ioLocker, syncContext);
                }
                else
                {
                    return new MemoryStream(Array.Empty<byte>(), 0, 0, false, false);
                }
            }

            public Stream OpenWrite()
            {
                this.OpenOrCreate(out var writeStream);
                this.ioLocker.EnterWriteLock();
                Interlocked.Exchange(ref this.memMappedFile, null)?.Dispose();
                this._mapped = false;
                var syncContext = SynchronizationContext.Current;
                if (syncContext == null)
                {
                    syncContext = new SynchronizationContext();
                    SynchronizationContext.SetSynchronizationContext(syncContext);
                }
                return new WrapperWriteStream(writeStream, this.ioLocker, syncContext);
            }

            sealed class WrapperWriteStream : WrapperReadStream
            {
                public WrapperWriteStream(Stream baseStream, ReaderWriterLockSlim lockSlim, SynchronizationContext syncContext)
                    : base(baseStream, lockSlim, syncContext) { }

                public override void Close()
                {
                    base.OriginalClose();
                    // We don't close the Filestream, keep it open.
                    this.syncContext.Post(new SendOrPostCallback(AttempGoingBackToTheThreadCreatedThisStreamForRelease), this.lockSlim);
                }

                private static void AttempGoingBackToTheThreadCreatedThisStreamForRelease(object? obj)
                {
                    if (obj is ReaderWriterLockSlim ioLocker && ioLocker.IsWriteLockHeld)
                    {
                        ioLocker.ExitWriteLock();
                    }
                }
            }

            class WrapperReadStream : Stream
            {
                public readonly Stream BaseStream;
                protected readonly ReaderWriterLockSlim lockSlim;
                protected readonly SynchronizationContext syncContext;

                public WrapperReadStream(Stream baseStream, ReaderWriterLockSlim lockSlim, SynchronizationContext syncContext)
                {
                    this.lockSlim = lockSlim;
                    this.BaseStream = baseStream;
                    this.syncContext = syncContext;
                }

                protected void OriginalClose() => base.Close();

                public override void Close()
                {
                    base.Close();
                    this.BaseStream.Close();
                    this.syncContext.Post(new SendOrPostCallback(AttempGoingBackToTheThreadCreatedThisStreamForRelease), this.lockSlim);
                }

                private static void AttempGoingBackToTheThreadCreatedThisStreamForRelease(object? obj)
                {
                    if (obj is ReaderWriterLockSlim ioLocker && ioLocker.IsReadLockHeld)
                    {
                        ioLocker.ExitReadLock();
                    }
                }

                public override bool CanRead => BaseStream.CanRead;

                public override bool CanSeek => BaseStream.CanSeek;

                public override bool CanWrite => BaseStream.CanWrite;

                public override long Length => BaseStream.Length;

                public override long Position { get => BaseStream.Position; set => BaseStream.Position = value; }

                public override void Flush()
                {
                    BaseStream.Flush();
                }

                public override int Read(byte[] buffer, int offset, int count)
                {
                    return BaseStream.Read(buffer, offset, count);
                }

                public override long Seek(long offset, SeekOrigin origin)
                {
                    return BaseStream.Seek(offset, origin);
                }

                public override void SetLength(long value)
                {
                    BaseStream.SetLength(value);
                }

                public override void Write(byte[] buffer, int offset, int count)
                {
                    BaseStream.Write(buffer, offset, count);
                }
            }
        }
    }
}
