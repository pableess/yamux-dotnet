namespace Yamux.Internal;

/// <summary>
/// Generates sequential stream ids either as even or odd numbers. 0 is reserved.
/// </summary>
class StreamIdGenerator
{
    private uint _seed;

    public StreamIdGenerator(bool even)
    {
        _seed = even ? 1u : 0u;
    }

    public uint Next()
    {
        var value = Interlocked.Add(ref _seed, 2);

        // Interlocked.Add performs the addition unchecked, so detect wraparound
        // manually before handing out an id that may collide with an existing stream.
        if (value <= 1)
        {
            throw new SessionException(SessionErrorCode.StreamsExhausted, "Streams exhausted");
        }

        return value - 1;
    }
}
