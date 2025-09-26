using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Yamux.Internal;

/// <summary>
/// Generates sequential ids either as even or odd numbers, starting at 1.  0 is reserved
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
        try
        {

            var value = checked(Interlocked.Add(ref _seed, 2));
            return value - 1;
        }
        catch (OverflowException)
        {
            throw new InvalidOperationException("Streams exhausted");
        }
    }
}