using System.Runtime.CompilerServices;
using Yamux.Protocol;

namespace Yamux.Internal
{
    internal class ConnectionReader
    {
        private readonly CancellationTokenSource _stoppingToken;
        private readonly ITransport _peer;

        public ConnectionReader(ITransport peer)
        {
            _stoppingToken = new CancellationTokenSource();
            _peer = peer ?? throw new ArgumentNullException(nameof(peer));
        }

        public async IAsyncEnumerable<FrameHeader> ReadFramesAsync([EnumeratorCancellation] CancellationToken cancel)
        {
            byte[] headerBuffer = new byte[FrameHeader.FrameHeaderSize];

            int bytesRead;

            while (!_stoppingToken.IsCancellationRequested)
            {
                try
                {
                    bytesRead = await this.ReadAll(headerBuffer, cancel);

                    if (bytesRead == 0)
                    {
                        // remote closed the connection
                        throw new SessionException(SessionErrorCode.StreamClosed, "Connection closed by remote", SessionTermination.Normal);
                    }
                }
                catch (OperationCanceledException)
                {
                    // TODO: add debug tracing
                }

                if (!_stoppingToken.IsCancellationRequested)
                {
                    yield return FrameHeader.Parse(headerBuffer);
                }
            }
        }

        public async ValueTask<int> ReadFramePayloadAsync(Memory<byte> data, CancellationToken cancel)
        {
            return await this.ReadAll(data, cancel);
        }

        private async ValueTask<int> ReadAll(Memory<byte> data, CancellationToken cancel)
        {
            if (data.IsEmpty)
                return 0;

            int requested = data.Length;
            int bytesRead = 0;
            do
            {
                var read = await _peer.ReadAsync(data.Slice(bytesRead, requested - bytesRead), cancel);
                if (read == 0)
                {
                    throw new SessionException(SessionErrorCode.StreamClosed, "Remote connection closed");
                }
                bytesRead += read;
            }
            while (bytesRead < requested);

            return bytesRead;
        }

        public void Stop()
        {
            _stoppingToken.Cancel();
        }
    }
}
