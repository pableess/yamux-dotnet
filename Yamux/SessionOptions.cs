using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Yamux;

public class SessionOptions
{
    public int AcceptBacklog { get; set; } = 256;

    public bool EnableKeepAlive { get; set; } = true;

    public TimeSpan KeepAliveInterval { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan StreamCloseTimeout { get; set; } = TimeSpan.FromMinutes(5);

    public TimeSpan StreamSendTimout { get; set; } = TimeSpan.FromSeconds(75);

    public SessionChannelOptions DefaultChannelOptions { get; set; } = new SessionChannelOptions();

    /// <summary>
    /// Enables statistics
    /// </summary>
    public bool EnableStatistics { get; set; }

    /// <summary>
    /// How often to sample the statistics
    /// </summary>
    public int StatisticsSampleInterval { get; set; } = 1000;
}
