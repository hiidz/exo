// The device connection's wire format, DECISIONS 5.7. Big-endian throughout,
// through BinaryPrimitives; never BinaryWriter, which is little-endian.

namespace ExoHub.Link;

static class Wire
{
    public static ReadOnlySpan<byte> Magic => "EXO\0"u8;
    // 2 added CURSOR_SHAPE, CURSOR_POS and TOUCH. Both roles share it, so a
    // version 1 app is refused on either connection.
    public const ushort Version = 2;

    public const int MaxHelloBody = 256, MaxSerial = 64;
    public const byte RoleCamera = 1, RoleDisplay = 2;
    public const byte Up = 1, Down = 2;

    // WELCOME status.
    public const byte Accepted = 0, VersionMismatch = 1, BadHello = 2, RoleTaken = 3;

    // Every message after the handshake: type, flags, reserved u16, body length u32.
    public const int HeaderSize = 8;
    public const int MaxBody = 4 << 20;

    // The display connection's cursor, hub to device (DECISIONS 11.1).
    // CURSOR_SHAPE: width, height, hotX, hotY as u16, then width * height
    // pixels, rows tight, each in byte order B, G, R, A, straight alpha.
    // CURSOR_POS: flags bit 0 visible; x, y i32, the hotspot in picture
    // pixels; the mouse update time i64, hub microseconds.
    public const byte TypeCursorShape = 4, TypeCursorPos = 5;
    public const byte FlagVisible = 1;
    // TOUCH, device to hub, on the display connection (FR-3.3).
    public const byte TypeTouch = 6;

    // L7, the hub to ExoCamera.dll link (DECISIONS 5.9). The same frozen
    // prefix and framing, its own version. The port is a constant because
    // the DLL reads no config; exo_link.h holds the same two numbers.
    public const int CameraPort = 27184;
    // 2 added IDLE. A version 1 DLL would skip it and freeze its last picture.
    public const ushort CameraVersion = 2;
    public const byte TypeFrame = 1;
    // The hub is alive and has no picture for the DLL, which shows its test
    // card. Sent after a second with nothing written. The body is one byte,
    // the reason, for the DLL's log.
    public const byte TypeIdle = 2;
    public const byte IdleNotWatching = 1, IdleNoFrame = 2;
}
