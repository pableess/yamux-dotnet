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
    /// Gets if the channel has been fully closed
    /// </summary>
    public bool IsClosed { get; }

    /// <summary>
    /// Aborts the channel immediately, sending a RST to the remote peer if the channel is not already closed
    /// </summary>
    public void Abort();

    /// <summary>
    /// Closes the channel.  This closes the channel for writing, but more data could still be read from the channel until the 
    /// remote peer acknowledges the close.  If you would like to wait until the remote peer has acknowledged the close, you can either continue reading
    /// until the pipe is completed, or call WaitForRemoteClose() or WhenRemoteCloseAsync()
    /// 
    /// </summary>
    public void Close();

    /// <summary>
    /// Waits until the remote peer has closed the channel
    /// </summary>
    /// <param name="timeout">A maximum amount of time to wait</param>
    /// <returns>true if the remote peer has closed, false if the operation has timed out</returns>
    public bool WaitForRemoteClose(TimeSpan timeout);

    /// <summary>
    /// Returns a task that completes when the remote peer has closed the channel.
    /// Use with caution as remote peer may fail to send proper close acknowledgement
    /// </summary>
    /// <param name="timeout"></param>
    /// <returns>True if the remote peer was closed, false if the timeout was reached</returns>
    public Task<bool> WhenRemoteCloseAsync(TimeSpan timeout);

    /// <summary>
    /// Ensures that all written data is flushed in the underlying session connection
    /// </summary>
    /// <param name="cancel"></param>
    /// <returns></returns>
    public Task FlushWritesAsync(CancellationToken? cancel);

    /// <summary>
    /// Gets the statistics for the channel, if statistics gathering is enabled
    /// </summary>
    public Statistics? Stats { get; }
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
