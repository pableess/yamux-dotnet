using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
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

            while (!_stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await this.ReadAll(headerBuffer, cancel);
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
                bytesRead += await _peer.ReadAsync(data.Slice(bytesRead, requested - bytesRead), cancel);
            }
            while (bytesRead > 0 && bytesRead < requested);

            return bytesRead;
        }

        public void Stop()
        {
            _stoppingToken.Cancel();
        }
    }
}
