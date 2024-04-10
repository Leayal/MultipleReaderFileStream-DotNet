using System;
using System.IO;

namespace Leayal.MultipleReaderFileStream
{
    /// <summary>Provides mechanism for multiple-read-single-write stream manager implementions.</summary>
    public interface IFileStreamBroker : IDisposable
    {
        /// <summary>Opens an associated writer stream to the broker.</summary>
        /// <returns>A <seealso cref="Stream"/> that can be written to.</returns>
        /// <remarks>
        /// <list type="bullet">
        /// <item>When you finish reading, you MUST close the stream by calling <seealso cref="Stream.Dispose()"/>.</item>
        /// <item>This method is thread-safe, however, the stream created by this method is NOT.</item>
        /// </list>
        /// </remarks>
        public Stream OpenWrite();

        /// <summary>Opens an associated writer stream to the broker.</summary>
        /// <returns>A <seealso cref="Stream"/> that can be written to.</returns>
        /// <remarks>
        /// <list type="bullet">
        /// <item>When you finish reading, you MUST close the stream by calling <seealso cref="Stream.Dispose()"/>.</item>
        /// <item>This method is thread-safe, however, the stream created by this method is NOT.</item>
        /// </list>
        /// </remarks>
        public Stream OpenRead();

        /// <summary>Close the underlying <seealso cref="FileStream"/> to the file on the disk.</summary>
        /// <remarks>You should only call this when all the streams have been disposed and the broker is deemed no longer necessarily to be kept open. Otherwise, you will likely encounter <seealso cref="ObjectDisposedException"/> in your code.</remarks>
        public new void Dispose();
    }
}
