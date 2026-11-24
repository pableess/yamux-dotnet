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
    /// Yamux fixes the wire-level initial window at 256 KB; setting a larger value
    /// immediately advertises the difference after the channel is opened. Values
    /// below 256 KB are rejected because the window cannot be made smaller than the
    /// protocol initial value.
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

    /// <summary>
    /// Validates the channel options and throws if any values are out of range.
    /// </summary>
    /// <exception cref="ValidationException">Thrown when <see cref="ReceiveWindowSize"/> is less than 256 KB,
    /// <see cref="ReceiveWindowUpperBound"/> is less than 256 KB, <see cref="ReceiveWindowSize"/> exceeds
    /// <see cref="ReceiveWindowUpperBound"/>, or <see cref="MaxDataFrameSize"/> is zero.</exception>
    public void Validate()
    {
        if (ReceiveWindowSize < Constants.Initial_Window_Size)
        {
            throw new ValidationException($"ReceiveWindowSize must be at least {Constants.Initial_Window_Size} bytes, but was {ReceiveWindowSize}");
        }
        if (ReceiveWindowUpperBound < Constants.Initial_Window_Size)
        {
            throw new ValidationException($"ReceiveWindowUpperBound must be at least {Constants.Initial_Window_Size} bytes, but was {ReceiveWindowUpperBound}");
        }
        if (ReceiveWindowSize > ReceiveWindowUpperBound)
        {
            throw new ValidationException("ReceiveWindowSize must not exceed the ReceiveWindowUpperBound");
        }
        if (MaxDataFrameSize == 0)
        {
            throw new ValidationException("MaxDataFrame must not be 0");
        }
    }
}
