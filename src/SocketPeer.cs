using System.Buffers;
using System.Runtime.InteropServices;

namespace Yamux
{
    /// <summary>
    /// An <see cref="ITransport"/> implementation that wraps a <see cref="System.Net.Sockets.Socket"/>.
    /// </summary>
    public class SocketPeer : ITransport
    {
        private readonly System.Net.Sockets.Socket _socket;
        private readonly System.Net.Sockets.SocketAsyncEventArgs _writeEventArgs = new();
        private readonly List<ArraySegment<byte>> _bufferList = new();
        private TaskCompletionSource<bool>? _writeTcs;

        /// <summary>
        /// Initializes a new instance of the <see cref="SocketPeer"/> class.
        /// </summary>
        /// <param name="socket">The underlying socket to use for transport.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="socket"/> is null.</exception>
        public SocketPeer(System.Net.Sockets.Socket socket)
        {
            _socket = socket ?? throw new ArgumentNullException(nameof(socket));
            _writeEventArgs.Completed += OnWriteCompleted;
        }

        /// <summary>
        /// This transport supports batched writes via <see cref="WriteAsync(ReadOnlySequence{byte}, CancellationToken)"/>.
        /// </summary>
        public bool SupportsBatching => true;

        private void OnWriteCompleted(object? sender, System.Net.Sockets.SocketAsyncEventArgs e)
        {
            e.BufferList = null;
            if (e.SocketError == System.Net.Sockets.SocketError.Success)
                _writeTcs?.TrySetResult(true);
            else
                _writeTcs?.TrySetException(new System.Net.Sockets.SocketException((int)e.SocketError));
        }

        /// <inheritdoc />
        public void Close()
        {
            _socket.Shutdown(System.Net.Sockets.SocketShutdown.Both);
            _socket.Close();
        }

        /// <inheritdoc />
        public void Dispose()
        {
            _writeEventArgs.Dispose();
            _socket.Dispose();
        }

        /// <inheritdoc />
        public async ValueTask<int> ReadAsync(Memory<byte> data, CancellationToken cancellationToken)
        {
            var read = await _socket.ReceiveAsync(data, System.Net.Sockets.SocketFlags.None, cancellationToken).ConfigureAwait(false);

            return read;
        }
        
        /// <inheritdoc />
        public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
        {
            if (data.IsEmpty)
            {
                return;
            }

            await _socket.SendAsync(data, System.Net.Sockets.SocketFlags.None, cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public ValueTask WriteAsync(ReadOnlySequence<byte> data, CancellationToken cancellationToken = default)
        {
            if (data.IsSingleSegment)
                return WriteAsync(data.First, cancellationToken);

            _bufferList.Clear();
            foreach (var segment in data)
            {
                if (MemoryMarshal.TryGetArray(segment, out var arraySegment))
                    _bufferList.Add(arraySegment);
                else
                    return SegmentedFallbackAsync(data, cancellationToken);
            }

            _writeTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _writeEventArgs.BufferList = _bufferList;

            if (_socket.SendAsync(_writeEventArgs))
            {
                if (cancellationToken.CanBeCanceled)
                {
                    var tcs = _writeTcs;
                    var reg = cancellationToken.Register(static state => ((TaskCompletionSource<bool>)state!).TrySetCanceled(), tcs);
                    return new ValueTask(WaitWithCleanupAsync(tcs.Task, reg));
                }

                return new ValueTask(_writeTcs.Task);
            }

            _writeEventArgs.BufferList = null;
            return _writeEventArgs.SocketError == System.Net.Sockets.SocketError.Success
                ? ValueTask.CompletedTask
                : ValueTask.FromException(new System.Net.Sockets.SocketException((int)_writeEventArgs.SocketError));
        }

        private static async Task WaitWithCleanupAsync(Task task, CancellationTokenRegistration reg)
        {
            using (reg)
            {
                await task.ConfigureAwait(false);
            }
        }

        private async ValueTask SegmentedFallbackAsync(ReadOnlySequence<byte> data, CancellationToken cancellationToken)
        {
            foreach (var segment in data)
                await WriteAsync(segment, cancellationToken).ConfigureAwait(false);
        }
    }
}