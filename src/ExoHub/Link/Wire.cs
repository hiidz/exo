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
}
