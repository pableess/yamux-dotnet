using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Yamux.Internal
{
    public readonly struct SemaphoreReleaser(SemaphoreSlim instance) : IDisposable
    {
        public void Dispose()
        {
            instance.Release();
        }
    }

}
