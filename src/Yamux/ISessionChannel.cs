using System;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Yamux;

public interface ISessionChannel : IDisposable
{
    /// <summary>
    /// Gets the ID for the stream
    /// </summary>
    public uint Id { get; }

    /// <summary>
    /// Fully closes the channel, waiting for the remote party to acknowledge the close if necessary
    /// </summary>
    /// <param name="timeout"></param>
    /// <param name="cancel"></param>
    /// <returns>A task that completes when the remote party has acknowledged the close</returns>
    /// <exception cref="InvalidOperationException"></exception>
    public Task CloseAsync(TimeSpan? timeout = null, CancellationToken? cancel = null);

    /// <summary>
    /// Ensures that all written data is flushed in the underlying session connection
    /// </summary>
    /// <param name="cancel"></param>
    /// <returns></returns>
    public Task FlushWritesAsync(CancellationToken? cancel);
}

public interface IWriteOnlySessionChannel : ISessionChannel
{
    /// <summary>
    /// Writes data to the channel
    /// The task will not complete until all of the data has been written to the session connection, which may require waiting to recieve window update(s)
    /// This is not an atomic operation as partial data may be written in the case of a failure
    /// </summary>
    /// <param name="buffer"></param>
    /// <param name="token"></param>
    /// <returns></returns>
    public ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken? token = null);
}

public interface IReadOnlySessionChannel : ISessionChannel
{
    /// <summary>
    /// Gets the pipe reader for the input channel
    /// </summary>
    public PipeReader Input { get; }
}

public interface IDuplexSessionChannel : ISessionChannel, IWriteOnlySessionChannel, IReadOnlySessionChannel
{
    /// <summary>
    /// Creates a new duplex stream for the channel.
    /// </summary>
    /// <returns></returns>
    public Stream AsStream(bool leaveOpen = false);
}
