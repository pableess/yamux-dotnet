namespace Yamux.Protocol;

/// <summary>
/// Defines the reason codes for session termination sent in GoAway frames.
/// </summary>
public enum SessionTermination : uint
{
    /// <summary>
    /// Normal session termination.
    /// </summary>
    Normal = 0x0,

    /// <summary>
    /// Session terminated due to a protocol error.
    /// </summary>
    ProtocolError = 0x1,

    /// <summary>
    /// Session terminated due to an internal error.
    /// </summary>
    InternalError = 0x2,
}

internal enum ProtocolVersion : byte
{
    Initial = 0x0
}

internal enum FrameType : byte
{
    Data = 0x0,
    WindowUpdate = 0x1,
    Ping = 0x2,
    GoAway = 0x3,
}

internal enum Flags : ushort
{
    None = 0x0,
    SYN = 0x1,
    ACK = 0x2,
    FIN = 0x4,
    RST = 0x8,
}
