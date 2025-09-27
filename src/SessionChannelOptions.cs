using System;
using System.Buffers;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.IO.Pipelines;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Yamux.Protocol;

namespace Yamux;

/// <summary>
/// Options for configuring a Yamux session channel.
/// </summary>
/// <remarks>
/// Includes settings for window size, frame size, and automatic tuning.
/// </remarks>
public class SessionChannelOptions
{
    public SessionChannelOptions() { }

    /// <summary>
    /// The initial size of the receive window for the channel. 
    /// Yamux spec indicates initial size is always 256kb, but this setting will immediately increase the size to the desired size 
    ///
    /// </summary>
    public uint ReceiveWindowSize { get; set; } = Constants.Initial_Window_Size;

    /// <summary>
    /// The maximum size of the receive window for the channel.
    /// </summary>
    public uint ReceiveWindowUpperBound { get; set; } = 16 * 1024 * 1024; // 16MiB

    /// <summary>
    /// The maximum size of a data frame payload. Data payloads that are bigger than this will be sent in separate frames.
    /// </summary>
    public uint MaxDataFrameSize { get; set; } = 16 * 1024; // 16KiB

    /// <summary>
    /// Automatically increase the window size (up to ReceiveWindowUpperBound) to optimize throughput and memory usage.
    /// </summary>
    public bool AutoTuneReceiveWindowSize { get; set; } = true;

    /// <summary>
    /// Enables statistics
    /// </summary>
    public bool EnableStatistics { get; set; }

    /// <summary>
    /// How often to sample the statistics
    /// </summary>
    public int StatisticsSampleInterval { get; set; } = 1000;

    public void Validate()
    {
        if (ReceiveWindowSize < (10 * 1024)) 
        {
            throw new ValidationException("ReceiveWindowSize must be greater than 258 KB");
        }
        if (AutoTuneReceiveWindowSize && ReceiveWindowSize > ReceiveWindowUpperBound) 
        {
            throw new ValidationException("ReceiveWindowSize must not exceed the ReceiveWindowUpperBound");
        }
        if (MaxDataFrameSize == 0)
        {
            throw new ValidationException("MaxDataFrame must not be 0");
        }
    }
}
