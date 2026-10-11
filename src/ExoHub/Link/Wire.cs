// The device connection's wire format, DECISIONS 5.7. Big-endian throughout,
// through BinaryPrimitives; never BinaryWriter, which is little-endian.

namespace ExoHub.Link;

static class Wire
{
    public static ReadOnlySpan<byte> Magic => "EXO\0"u8;
    public const ushort Version = 1;

    public const int MaxHelloBody = 256, MaxSerial = 64;
    public const byte RoleCamera = 1, RoleDisplay = 2;
    public const byte Up = 1, Down = 2;

    // WELCOME status.
    public const byte Accepted = 0, VersionMismatch = 1, BadHello = 2, RoleTaken = 3;

    // Every message after the handshake: type, flags, reserved u16, body length u32.
    public const int HeaderSize = 8;
    public const int MaxBody = 4 << 20;

    // L7, the hub to ExoCamera.dll link (DECISIONS 5.9). The same frozen
    // prefix and framing, its own version. The port is a constant because
    // the DLL reads no config; exo_link.h holds the same two numbers.
    public const int CameraPort = 27184;
    public const ushort CameraVersion = 1;
    public const byte TypeFrame = 1;
}
