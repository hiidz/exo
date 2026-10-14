// The device connection's wire format, DECISIONS 5.7. Big-endian throughout,
// through BinaryPrimitives; never BinaryWriter, which is little-endian.

namespace ExoHub.Link;

static class Wire
{
    public static ReadOnlySpan<byte> Magic => "EXO\0"u8;
    // 2 added CURSOR_SHAPE, CURSOR_POS and TOUCH. Both roles share it, so a
    // version 1 app is refused on either connection. 3 added HELLO's
    // rotation byte after the serial, clockwise quarter turns (DECISIONS 6.4).
    // 4: the role is picked on the phone, which names itself with an
    // identity it made, and each side states the oldest version it speaks.
    public const ushort Version = 4, Oldest = 4;

    // HELLO is frozen up to its body: magic, the newest version the device
    // speaks u16, body length u16. From 4 on the body starts with the oldest
    // version it speaks u16, then role u8, identity [16], name length u8 and
    // UTF-8 name, rotation u8. Later versions only append to it.
    public const int MaxHelloBody = 256, IdSize = 16, MaxName = 64;
    public const byte RoleCamera = 1, RoleDisplay = 2;

    // WELCOME: magic, version u16, status u8, body length u16, body. Accepted
    // carries the version both sides now speak and the session ID; a refusal
    // carries the hub's newest version and UTF-8 text. The phone turns each
    // status into a sentence. On VersionMismatch it compares the hub's
    // version with its own oldest to say which side to update. RoleTaken's
    // text is the holder's name; the rest are detail for the phone's log.
    public const byte Accepted = 0, VersionMismatch = 1, BadHello = 2, RoleTaken = 3;
    public const byte CameraUnavailable = 4, DisplayUnavailable = 5;

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
