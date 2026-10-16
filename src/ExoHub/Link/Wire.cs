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
    // 5: the display role's HELLO carries its mode, rate and panel size.
    // 6: the display phone acks each frame it has decoded, and the hub caps
    // the frames in flight; a phone that sent no acks would freeze at the cap.
    // 7: the camera role's HELLO carries the size and rate set on the phone.
    // 8: over Wi-Fi the hub may answer HELLO with Pending first, while the
    // PC asks its user, then Accepted or Denied. Only Wi-Fi sends it, and
    // no 7 app reaches Wi-Fi, so 7 is still spoken.
    // 9: the display phone sends PAUSE when its display goes out of view and
    // RESUME when it shows again unchanged. A phone below 9 never pauses; it
    // drops the connection instead, so 7 and 8 are still spoken.
    // 10: every Wi-Fi connection is TLS, with pairing (Pairing.cs). Over USB
    // nothing changed, so 7 to 9 still speak there; a plain HELLO over Wi-Fi
    // is refused as a version mismatch.
    public const ushort Version = 10, Oldest = 7, TlsFrom = 10;

    // HELLO is frozen up to its body: magic, the newest version the device
    // speaks u16, body length u16. From 4 on the body starts with the oldest
    // version it speaks u16, then role u8, identity [16], name length u8 and
    // UTF-8 name, rotation u8. Later versions only append to it. From 5 the
    // display role appends width u16, height u16 (portrait is taller than
    // wide), fps u8 (30 or 60), then the panel's width and height in mm u16,
    // 0 when unknown. From 7 the camera role appends width u16, height u16
    // (landscape, as the sensor gives it) and fps u8 (30 or 60). From 10
    // both roles then append pairing u8, 1 when the phone holds no pin for
    // this PC and so asks for the code, and its commitment [32], SHA-256 of
    // its pairing nonce. Read over Wi-Fi only.
    public const int MaxHelloBody = 256, IdSize = 16, MaxName = 64, DisplayFields = 9, CameraFields = 5;
    public const int PairFields = 1 + 32, NonceSize = 32;
    public const byte RoleCamera = 1, RoleDisplay = 2;

    // WELCOME: magic, version u16, status u8, body length u16, body. Accepted
    // carries the version both sides now speak and the session ID; a refusal
    // carries the hub's newest version and UTF-8 text. The phone turns each
    // status into a sentence. On VersionMismatch it compares the hub's
    // version with its own oldest to say which side to update. RoleTaken's
    // text is the holder's name; the rest are detail for the phone's log.
    public const byte Accepted = 0, VersionMismatch = 1, BadHello = 2, RoleTaken = 3;
    public const byte CameraUnavailable = 4, DisplayUnavailable = 5;
    // From 8, Wi-Fi only. Pending is not an answer: its text is the PC's
    // name, and the real WELCOME follows once the PC's user answers. Denied:
    // the PC's user said no, and the phone stops asking until its user
    // starts the role again. From 10 Pending's body starts with the hub's
    // pairing nonce [32], then the name, and the phone answers it with its
    // own nonce [32], raw, which must hash to HELLO's commitment. Both sides
    // then show the code (Pairing.Code).
    public const byte Pending = 6, Denied = 7;

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
    // ACK, device to hub, on the display connection: seq u32, every FRAME
    // up to and including it is done, decoded and on its way to the screen
    // or discarded. Cumulative, so a frame the decoder drops is covered by
    // the next one's ack.
    public const byte TypeAck = 7;
    // From 9, device to hub, on the display connection, no body. PAUSE: the
    // display is out of view; the hub sends no frames and no cursor, and
    // the connection, and so the monitor, stays. RESUME: it shows again
    // with the same mode; the hub starts again with a forced IDR. A changed
    // mode connects again instead (PLAN.md, Decide, Phone and protocol).
    public const byte TypePause = 8, TypeResume = 9;

    // Discovery over Wi-Fi, UDP on DiscoveryPort, the one fixed LAN port
    // (PLAN.md, Wi-Fi as built in step 4). Its own version. QUERY, phone to
    // hub, broadcast or sent to a typed IP: magic, version u16, kind u8.
    // ANSWER, hub to the query's sender: magic, version u16, kind u8, the
    // Wi-Fi listener's TCP port u16, the session ID u64, name length u8 and
    // the PC's UTF-8 name, then the newest device protocol the hub speaks
    // u16, so a phone can tell a hub too old for TLS before it connects (an
    // answer without it is from one). A packet of another kind or version
    // is ignored.
    public const int DiscoveryPort = 27185;
    public const ushort DiscoveryVersion = 1;
    public const byte KindQuery = 1, KindAnswer = 2;

    // L7, the hub to ExoCamera.dll link (DECISIONS 5.9). The same frozen
    // prefix and framing, its own version. The port is a constant because
    // the DLL reads no config; exo_link.h holds the same two numbers.
    public const int CameraPort = 27184;
    // 2 added IDLE. A version 1 DLL would skip it and freeze its last picture.
    // 3: HELLO's body is the DLL's process ID u32, then its purpose u8. An
    // accepted WELCOME's body is the session u64, then the format the
    // camera advertises, width, height and fps u32 each. The DLL asks for it
    // when the source is created, on a connection of purpose PurposeFormat,
    // which the hub closes after WELCOME.
    public const ushort CameraVersion = 3;
    public const byte PurposeFrames = 0, PurposeFormat = 1;
    public const byte TypeFrame = 1;
    // The hub is alive and has no picture for the DLL, which shows its test
    // card. Sent after a second with nothing written. The body is one byte,
    // the reason, for the DLL's log.
    public const byte TypeIdle = 2;
    public const byte IdleNotWatching = 1, IdleNoFrame = 2;
}
