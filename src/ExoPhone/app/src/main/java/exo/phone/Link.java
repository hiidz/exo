package exo.phone;

import android.content.Context;
import android.graphics.Bitmap;
import android.net.LocalSocket;
import android.net.LocalSocketAddress;
import android.net.Network;
import android.util.Log;

import java.io.BufferedInputStream;
import java.io.BufferedOutputStream;
import java.io.DataInputStream;
import java.io.DataOutputStream;
import java.io.File;
import java.io.Closeable;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.io.PrintWriter;
import java.net.InetAddress;
import java.net.InetSocketAddress;
import java.net.Socket;
import java.nio.ByteBuffer;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.List;
import java.util.concurrent.Executors;
import java.util.concurrent.ScheduledExecutorService;
import java.util.concurrent.TimeUnit;

/**
 * A connection to the hub: over USB, localabstract:exo, which adb reverse
 * carries to the hub's port; over Wi-Fi, the port in the picked PC's
 * discovery answer (Via, Discovery). Connects, runs the handshake in DECISIONS 5.7, then serves
 * its role until the connection ends, and starts again a second later,
 * forever. The one exception is a version refusal, which does not fix
 * itself, so the loop stops. Every change of state goes to the listener as
 * a sentence for the screen.
 *
 * Display role: reads FRAMEs into a decoder. Camera role: a CameraRole per
 * connection, whose frames this thread writes up, while a second thread
 * reads the PONGs coming down.
 *
 * Everything here blocks, so it runs on its own thread. stop() closes the
 * socket from another thread, which makes the blocked read throw.
 */
final class Link implements Runnable {
    interface Listener {
        /**
         * What the user should know, as a sentence: waiting for the PC, or why
         * the PC refused. Null once the hub accepts. Only on a change. Link
         * thread.
         */
        void onStatus(String sentence);

        /**
         * A decoder for one accepted connection, not yet started. name tells
         * runs apart; the decoder acks each frame it outputs through acks.
         */
        Decoder newDecoder(String name, java.util.function.LongConsumer acks);

        /** The camera for one accepted connection, not yet started. */
        CameraRole newCamera(String name);

        /** Where to write a per-connection log file called name. */
        File file(String name);

        /**
         * HELLO's rotation, clockwise quarter turns the hub applies to the
         * picture. Asked before each HELLO. Only the camera role turns.
         */
        default int rotation() throws IOException {
            return 0;
        }

        /**
         * What HELLO carries after the rotation: the display role's mode
         * (DisplaySettings) or the camera role's size and rate (CameraSettings).
         */
        default byte[] helloTail() {
            return new byte[0];
        }

        /**
         * The hub's pointer shape (DECISIONS 11.1). hotX and hotY are the
         * hotspot within the bitmap. Link thread. A default method: the
         * camera role has no cursor and need not implement it.
         */
        default void onCursorShape(Bitmap shape, int hotX, int hotY) {
        }

        /**
         * The hotspot in picture pixels, or hidden. hubUs is the hub's mouse
         * update time on the PONG t2 clock, 0 when the link makes one up.
         * Link thread.
         */
        default void onCursorPos(boolean visible, int x, int y, long hubUs) {
        }

        /** A display connection ended. name is the one newDecoder got. Link thread. */
        default void onLinkEnded(String name) {
        }
    }

    static final byte[] MAGIC = {'E', 'X', 'O', 0};
    // 2 added CURSOR_SHAPE, CURSOR_POS and TOUCH on the display connection.
    // 3 added HELLO's rotation byte, after the serial (DECISIONS 6.4).
    // 4: the role is picked here, HELLO carries this app's identity and
    // name in place of the serial, and each side states the oldest version
    // it speaks. 5: the display role's HELLO carries its mode, rate and
    // panel size. 6: the display connection acks each frame (ACK), and
    // the hub caps the frames in flight. 7: the camera role's HELLO carries
    // its size and rate. 8: over Wi-Fi the hub may answer PENDING, then
    // ACCEPTED or DENIED. HELLO's layout is in the hub's Wire.cs.
    static final int VERSION = 8, OLDEST = 7;
    static final int ROLE_CAMERA = 1, ROLE_DISPLAY = 2;
    static final int ACCEPTED = 0, VERSION_MISMATCH = 1, BAD_HELLO = 2, ROLE_TAKEN = 3;
    static final int CAMERA_UNAVAILABLE = 4, DISPLAY_UNAVAILABLE = 5;
    // 8, over Wi-Fi only: the PC asks its user, then answers again; or its
    // user said no, and this phone stops asking until its role starts again.
    static final int PENDING = 6, DENIED = 7;
    private static final int ASK_MS = 120_000;
    static final String WAITING = "Waiting for the PC. Plug this phone in by USB, with Exo running on the PC.";
    static final String NO_WIFI = "This phone is not on Wi-Fi. Join the PC's network.";
    static final String NEEDS_PERMISSION = "Exo needs the Nearby devices permission to find the PC. Pick Wi-Fi again to allow it.";
    static final String BLOCKED = "Android does not let Exo use the network. Allow network data for Exo in Settings, Apps.";
    private static final int CONNECT_TIMEOUT_MS = 3000, FIND_MS = 1000;
    static final int FRAME = 1, PING = 2, PONG = 3, FLAG_KEYFRAME = 1, FLAG_CONFIG = 2;
    static final int CURSOR_SHAPE = 4, CURSOR_POS = 5, TOUCH = 6, ACK = 7, FLAG_VISIBLE = 1;
    private static final int PING_MS = 1000, SILENCE_MS = 3000;
    static final int FRAME_FIELDS = 4 + 3 * 8; // seq, present, encoded, sent
    static final int HEADER_SIZE = 8, MAX_BODY = 4 << 20;
    private static final int WELCOME_TIMEOUT_MS = 2000, RETRY_MS = 1000;

    private final Identity me;
    private final int version, oldest;
    private final int role;
    private final Listener listener;
    private final Thread thread = new Thread(this, "link");
    private volatile boolean stopped;
    private volatile Conn socket;
    private final Context context;
    private final Via via;
    private boolean refused;
    private int attempts;
    private String lastFailure;
    private String lastStatus = "";  // never a sentence, so the first status is passed on
    private int connections;

    /**
     * version is VERSION except to prove a mismatch is refused; then this end
     * speaks only that version. role is ROLE_CAMERA or ROLE_DISPLAY.
     */
    Link(Context context, Identity me, int version, int role, Listener listener) {
        this.context = context;
        this.via = Via.load(context);
        this.me = me;
        this.version = version;
        this.oldest = version == VERSION ? OLDEST : version;
        this.role = role;
        this.listener = listener;
    }

    private void status(String sentence) {
        if (java.util.Objects.equals(sentence, lastStatus)) return;
        lastStatus = sentence;
        listener.onStatus(sentence);
    }

    /** The hub's refusal as the user should read it. text is the WELCOME body. */
    private String sentence(int status, int hubVersion, String text) {
        switch (status) {
            case VERSION_MISMATCH:
                return hubVersion < oldest ? "Exo on the PC is too old for this app. Update Exo on the PC."
                        : "This app is too old for Exo on the PC. Update the Exo app.";
            case ROLE_TAKEN:
                return (role == ROLE_DISPLAY ? "The PC's display" : "The PC's webcam") + " is in use by " + text + ".";
            case CAMERA_UNAVAILABLE:
                return "Exo's webcam is not set up on the PC. Reinstall Exo on the PC.";
            case DISPLAY_UNAVAILABLE:
                return "Exo's display driver is not working on the PC. Reinstall Exo on the PC.";
            case DENIED:
                return "The PC did not allow this phone. Start again to ask again.";
            case BAD_HELLO:
                return "Exo on the PC did not understand this app. Update Exo on both.";
            default:
                return "Exo on the PC refused this phone, error " + status + ". Update Exo on both.";
        }
    }

    void start() {
        thread.start();
    }

    void stop() {
        stopped = true;
        Conn s = socket;
        if (s != null) {
            try {
                s.close();
            } catch (IOException ignored) {
            }
        }
        thread.interrupt(); // in case it is sleeping between attempts
        // The connection's decoder lets go of the Surface on the way out.
        try {
            thread.join(3000);
        } catch (InterruptedException e) {
            Thread.currentThread().interrupt();
        }
    }

    @Override
    public void run() {
        while (!stopped && !refused) {
            attempts++;
            try {
                session();
            } catch (IOException e) {
                // Failures repeat every second, so a failure is logged when
                // it differs from the last one logged, and every 30th
                // attempt. The end of an accepted connection always differs.
                String what = e.toString();
                if (!what.equals(lastFailure) || attempts % 30 == 0) {
                    Log.i(Codecs.TAG, "link attempt " + attempts + ": " + what, e);
                    lastFailure = what;
                }
                if (!stopped) status(e instanceof NoWifi ? NO_WIFI : blocked(e) ? blockedSentence(context) : waiting());
            }
            if (stopped || refused) break;
            try {
                Thread.sleep(RETRY_MS);
            } catch (InterruptedException e) {
                break;
            }
        }
        Log.i(Codecs.TAG, "link stopped" + (refused ? " after a refusal" : ""));
    }

    /** EOF before WELCOME: adbd took the connection but no hub is behind it. */
    private static final class NoHub extends IOException {
        NoHub(String what) {
            super(what);
        }
    }

    /** No Wi-Fi network to look on. */
    private static final class NoWifi extends IOException {
        NoWifi() {
            super("no Wi-Fi network");
        }
    }

    /**
     * Android refused the socket: the per-app network switch is off (policy
     * REJECT_ALL, seen on the S21 as "socket failed: ECONNREFUSED"), or the
     * local network rule (seen as "sendto failed: EPERM" with
     * RESTRICT_LOCAL_NETWORK on) or another permission is missing.
     */
    static boolean blocked(IOException e) {
        String m = String.valueOf(e.getMessage());
        return m.startsWith("socket failed") || m.contains("EPERM") || m.contains("EACCES");
    }

    static String blockedSentence(Context context) {
        return Discovery.needsPermission(context) ? NEEDS_PERMISSION : BLOCKED;
    }

    /** One connection, over a LocalSocket (USB) or a Socket (Wi-Fi). */
    private interface Conn extends Closeable {
        InputStream in() throws IOException;

        OutputStream out() throws IOException;

        void timeout(int ms) throws IOException;
    }

    private String waiting() {
        if (!via.wifi) return WAITING;
        return via.target() == null ? "Pick the PC to connect to over Wi-Fi."
                : "Looking for " + via.target() + " on Wi-Fi. Exo must be running on the PC, on this network.";
    }

    /**
     * Connects as the phone's home screen picked. Published to socket before
     * connecting, so stop() can end a connect in progress. Null when stopped.
     */
    private Conn open() throws IOException {
        if (!via.wifi) {
            LocalSocket ls = new LocalSocket();
            Conn c = new Conn() {
                public InputStream in() throws IOException { return ls.getInputStream(); }
                public OutputStream out() throws IOException { return ls.getOutputStream(); }
                public void timeout(int ms) throws IOException { ls.setSoTimeout(ms); }
                public void close() throws IOException { ls.close(); }
            };
            socket = c;
            if (stopped) {
                c.close();
                return null;
            }
            try {
                ls.connect(new LocalSocketAddress("exo", LocalSocketAddress.Namespace.ABSTRACT));
            } catch (IOException e) {
                c.close();
                throw e;
            }
            return c;
        }
        if (via.target() == null) throw new IOException("no PC picked");
        Network net = Discovery.wifi(context);
        if (net == null) throw new NoWifi();
        InetAddress to = via.ip != null ? InetAddress.getByName(via.ip) : null;
        List<Discovery.Answer> found = Discovery.find(net, to, FIND_MS,
                l -> to != null || l.get(l.size() - 1).name.equalsIgnoreCase(via.pc));
        Discovery.Answer hub = null;
        for (Discovery.Answer a : found) if (to != null || a.name.equalsIgnoreCase(via.pc)) hub = a;
        if (hub == null) throw new IOException("no answer from " + via.target());
        Socket t = net.getSocketFactory().createSocket();
        Conn c = new Conn() {
            public InputStream in() throws IOException { return t.getInputStream(); }
            public OutputStream out() throws IOException { return t.getOutputStream(); }
            public void timeout(int ms) throws IOException { t.setSoTimeout(ms); }
            public void close() throws IOException { t.close(); }
        };
        socket = c;
        if (stopped) {
            c.close();
            return null;
        }
        try {
            // Small messages such as ACK and PING go at once.
            t.setTcpNoDelay(true);
            t.connect(new InetSocketAddress(hub.address, hub.port), CONNECT_TIMEOUT_MS);
        } catch (IOException e) {
            c.close();
            throw e;
        }
        Log.i(Codecs.TAG, String.format("link over Wi-Fi to \"%s\" at %s:%d, session %016x", hub.name,
                hub.address.getHostAddress(), hub.port, hub.session));
        return c;
    }

    private void session() throws IOException {
        try (Conn s = open()) {
            if (s == null) return;
            long connectedNs = System.nanoTime();
            s.timeout(WELCOME_TIMEOUT_MS);
            DataInputStream in = new DataInputStream(new BufferedInputStream(s.in()));
            DataOutputStream out = new DataOutputStream(new BufferedOutputStream(s.out()));

            byte[] myName = me.nameBytes();
            byte[] tail = listener.helloTail();
            out.write(MAGIC);
            out.writeShort(version);
            out.writeShort(2 + 1 + Identity.ID_SIZE + 1 + myName.length + 1 + tail.length);
            out.writeShort(oldest);
            out.writeByte(role);
            out.write(me.id);
            out.writeByte(myName.length);
            out.write(myName);
            out.writeByte(listener.rotation());
            out.write(tail);
            out.flush();

            byte[] magic = new byte[4];
            try {
                in.readFully(magic);
            } catch (java.io.EOFException e) {
                throw new NoHub("connected, then EOF before WELCOME: no hub behind the reverse");
            }
            if (!java.util.Arrays.equals(magic, MAGIC)) {
                throw new IOException("WELCOME has bad magic");
            }
            int hubVersion = in.readUnsignedShort();
            int status = in.readUnsignedByte();
            byte[] body = new byte[in.readUnsignedShort()];
            in.readFully(body);
            // Over Wi-Fi the PC may ask its user first; the answer is the
            // next WELCOME. Waiting longer than ASK_MS gives up and asks
            // again, which joins the same question on the PC.
            if (status == PENDING) {
                String pc = new String(body, StandardCharsets.UTF_8);
                Log.i(Codecs.TAG, "link pending: " + pc + " is asking its user");
                status("Allow this phone on " + pc + ": Exo asks there.");
                s.timeout(ASK_MS);
                in.readFully(magic);
                if (!java.util.Arrays.equals(magic, MAGIC)) throw new IOException("WELCOME has bad magic");
                hubVersion = in.readUnsignedShort();
                status = in.readUnsignedByte();
                body = new byte[in.readUnsignedShort()];
                in.readFully(body);
            }
            double welcomeMs = (System.nanoTime() - connectedNs) / 1e6;

            if (status != ACCEPTED) {
                String text = new String(body, StandardCharsets.UTF_8);
                Log.e(Codecs.TAG, "link REFUSED by hub, status " + status + ", hub protocol " + hubVersion
                        + ", ours " + oldest + " to " + version + ": " + text);
                // Any other refusal may clear, so the loop tries again.
                refused = status == VERSION_MISMATCH || status == DENIED;
                status(sentence(status, hubVersion, text));
                return;
            }
            if (hubVersion < oldest || hubVersion > version) {
                Log.e(Codecs.TAG, "link REFUSED the hub: it accepted at protocol " + hubVersion
                        + ", this app speaks " + oldest + " to " + version);
                refused = true;
                status("Exo on the PC and this app do not agree on a version. Update Exo on both.");
                return;
            }
            Log.i(Codecs.TAG, String.format("link accepted after %d attempts, WELCOME in %.1fms, protocol %d,"
                    + " session %016x, identity %s \"%s\"", attempts, welcomeMs, hubVersion,
                    ByteBuffer.wrap(body).getLong(), Identity.hex(me.id), me.name));
            attempts = 0;
            lastFailure = null;
            status(null);

            // Nothing at all for this long and the connection is dead. PONGs
            // arrive every second, so a static screen does not trip it.
            s.timeout(SILENCE_MS);
            String name = "link-" + (++connections);
            if (role == ROLE_CAMERA) {
                cameraSession(s, in, out, name);
                return;
            }
            Decoder decoder = listener.newDecoder(name, this::sendAck);
            decoder.start();
            Counts counts = new Counts();
            ArrayList<long[]> pongs = new ArrayList<>();
            ScheduledExecutorService pinger = Executors.newSingleThreadScheduledExecutor();
            pinger.scheduleAtFixedRate(() -> ping(out), 0, PING_MS, TimeUnit.MILLISECONDS);
            upward = new Upward(pinger, out);
            try {
                readMessages(in, decoder, counts, pongs);
            } finally {
                upward = null;
                pinger.shutdownNow();
                // The next connection replays the shape and position.
                listener.onCursorPos(false, 0, 0, 0);
                Log.i(Codecs.TAG, "link connection " + connections + " ended: " + counts + ", pongs " + pongs.size());
                decoder.stop();
                writePongs(listener.file(name + "-pongs.csv"), pongs);
                listener.onLinkEnded(name);
            }
        }
    }

    /**
     * The camera role's connection. This thread writes the camera's frames
     * up; a reader thread takes the PONGs, and its silence timeout or a read
     * error closes the socket, which ends the writing too. A camera failure
     * ends the connection, and the next one starts a new camera.
     */
    private void cameraSession(Conn s, DataInputStream in, DataOutputStream out, String name)
            throws IOException {
        CameraRole camera = listener.newCamera(name);
        ArrayList<long[]> pongs = new ArrayList<>();
        Thread reader = new Thread(() -> {
            try {
                readMessages(in, null, new Counts(), pongs);
            } catch (IOException e) {
                Log.i(Codecs.TAG, "link camera reader ended: " + e);
            } finally {
                try {
                    s.close();
                } catch (IOException ignored) {
                }
            }
        }, "link reader");
        ScheduledExecutorService pinger = Executors.newSingleThreadScheduledExecutor();
        long seq = 0, sent = 0, bytes = 0;
        double waitMaxMs = 0, writeMaxMs = 0, waitSumMs = 0;
        long windowStart = System.nanoTime();
        try {
            camera.start();
            reader.start();
            pinger.scheduleAtFixedRate(() -> ping(out), 0, PING_MS, TimeUnit.MILLISECONDS);
            while (!stopped) {
                if (camera.failure() != null) throw new IOException("camera: " + camera.failure());
                CameraRole.Frame f;
                try {
                    f = camera.take(100);
                } catch (InterruptedException e) {
                    throw new IOException("interrupted");
                }
                if (f == null) continue;
                long t0 = System.nanoTime();
                synchronized (out) {
                    out.writeByte(FRAME);
                    out.writeByte(f.flags);
                    out.writeShort(0);
                    out.writeInt(FRAME_FIELDS + f.data.length);
                    out.writeInt((int) ++seq);
                    out.writeLong(f.presentUs);
                    out.writeLong(f.encodedUs);
                    out.writeLong(System.nanoTime() / 1000);
                    out.write(f.data);
                    out.flush();
                }
                long t1 = System.nanoTime();
                double waitMs = (t0 / 1000 - f.encodedUs) / 1000.0;
                waitSumMs += waitMs;
                waitMaxMs = Math.max(waitMaxMs, waitMs);
                writeMaxMs = Math.max(writeMaxMs, (t1 - t0) / 1e6);
                sent++;
                bytes += f.data.length;
                if (t1 - windowStart >= 5_000_000_000L) {
                    Log.i(Codecs.TAG, String.format("link camera last 5s: %d frames sent, %.2f Mbps, encoded to sent "
                                    + "%.2fms mean %.2fms max, write max %.2fms", sent, bytes * 8 / ((t1 - windowStart) / 1e3),
                            waitSumMs / sent, waitMaxMs, writeMaxMs));
                    windowStart = t1;
                    sent = bytes = 0;
                    waitSumMs = waitMaxMs = writeMaxMs = 0;
                }
            }
        } finally {
            pinger.shutdownNow();
            camera.stop();
            s.close();
            try {
                reader.join(1000);
            } catch (InterruptedException e) {
                Thread.currentThread().interrupt();
            }
            Log.i(Codecs.TAG, "link camera connection " + connections + " ended after " + seq + " frames, pongs "
                    + pongs.size());
            writePongs(listener.file(name + "-pongs.csv"), pongs);
        }
    }

    /**
     * The display connection's writer thread and stream, together, so the UI
     * thread reads both or neither. Null with no display connection.
     */
    private static final class Upward {
        final ScheduledExecutorService writer;
        final DataOutputStream out;

        Upward(ScheduledExecutorService writer, DataOutputStream out) {
            this.writer = writer;
            this.out = out;
        }
    }

    private volatile Upward upward;

    /**
     * Any thread, never blocks: the write happens on the connection's writer
     * thread, the one PING uses. False when there is no display connection
     * to send it on (FR-3.3).
     */
    boolean sendTouch(byte[] body) {
        return sendUp(TOUCH, body);
    }

    /**
     * Every FRAME up to seq is done: decoded, or discarded. Cumulative, so a
     * frame the decoder drops is covered by the next one's. Any thread, in
     * order: everything up goes through the one writer thread.
     */
    void sendAck(long seq) {
        sendUp(ACK, ByteBuffer.allocate(4).putInt((int) seq).array());
    }

    private boolean sendUp(int type, byte[] body) {
        Upward u = upward;
        if (u == null) return false;
        try {
            u.writer.execute(() -> {
                try {
                    synchronized (u.out) {
                        u.out.writeByte(type);
                        u.out.writeByte(0);
                        u.out.writeShort(0);
                        u.out.writeInt(body.length);
                        u.out.write(body);
                        u.out.flush();
                    }
                } catch (IOException e) {
                    // The reader sees the same failure and ends the connection.
                }
            });
        } catch (java.util.concurrent.RejectedExecutionException e) {
            return false; // the connection ended between the read and the call
        }
        return true;
    }

    /** On the pinger's thread. t1 is System.nanoTime, the clock every phone time here uses. */
    private void ping(DataOutputStream out) {
        try {
            synchronized (out) {
                out.writeByte(PING);
                out.writeByte(0);
                out.writeShort(0);
                out.writeInt(8);
                out.writeLong(System.nanoTime());
                out.flush();
            }
        } catch (IOException e) {
            // The reader sees the same failure and ends the connection.
        }
    }

    /** Raw t1 to t4; the offline analysis relates the clocks (DECISIONS 5.7). */
    private static void writePongs(File f, ArrayList<long[]> pongs) {
        try (PrintWriter w = new PrintWriter(f)) {
            w.println("t1_ns,t2_us,t3_us,t4_ns");
            for (long[] p : pongs) w.printf("%d,%d,%d,%d%n", p[0], p[1], p[2], p[3]);
        } catch (IOException e) {
            Log.e(Codecs.TAG, "cannot write " + f, e);
        }
    }

    /** Per connection. Read by the log line when the connection ends. */
    private static final class Counts {
        int received, discardedBeforeKeyframe, flagMismatch, paramChanges, skipped;
        int cursorShapes, cursorPositions;
        long seqGaps, lastSeq = -1;
        final ArrayList<Long> scanNs = new ArrayList<>();

        @Override
        public String toString() {
            return String.format("received %d, discarded before keyframe %d, seq gaps %d,"
                            + " CONFIG flag disagreeing with the bytes %d, SPS/PPS changes %d,"
                            + " other messages skipped %d, cursor shapes %d, cursor positions %d", received,
                    discardedBeforeKeyframe, seqGaps, flagMismatch, paramChanges, skipped, cursorShapes,
                    cursorPositions) + ", header scan ms " + scanStats();
        }

        String scanStats() {
            if (scanNs.isEmpty()) return "n=0";
            long[] s = new long[scanNs.size()];
            for (int i = 0; i < s.length; i++) s[i] = scanNs.get(i);
            java.util.Arrays.sort(s);
            return String.format("median=%.2f p95=%.2f max=%.2f", s[s.length / 2] / 1e6, s[(int) (s.length * 0.95)] / 1e6,
                    s[s.length - 1] / 1e6);
        }
    }

    /**
     * Width, height, hotX, hotY, then pixels in byte order B, G, R, A with
     * straight alpha. createBitmap takes unpremultiplied ARGB ints, which is
     * straight alpha, so each pixel only needs its bytes reordered.
     */
    private void readCursorShape(DataInputStream in, int length) throws IOException {
        int w = in.readUnsignedShort(), h = in.readUnsignedShort();
        int hotX = in.readUnsignedShort(), hotY = in.readUnsignedShort();
        if (w == 0 || h == 0 || length != 8 + w * h * 4) {
            throw new IOException("protocol error: cursor shape " + w + "x" + h + " in " + length + " bytes");
        }
        byte[] px = new byte[w * h * 4];
        in.readFully(px);
        int[] argb = new int[w * h];
        for (int i = 0; i < argb.length; i++) {
            int b = px[i * 4] & 0xFF, g = px[i * 4 + 1] & 0xFF, r = px[i * 4 + 2] & 0xFF, a = px[i * 4 + 3] & 0xFF;
            argb[i] = a << 24 | r << 16 | g << 8 | b;
        }
        Log.i(Codecs.TAG, "cursor shape " + w + "x" + h + " hot (" + hotX + "," + hotY + ")");
        listener.onCursorShape(Bitmap.createBitmap(argb, w, h, Bitmap.Config.ARGB_8888), hotX, hotY);
    }

    private void readMessages(DataInputStream in, Decoder decoder, Counts counts, ArrayList<long[]> pongs)
            throws IOException {
        byte[] skip = new byte[256];
        ByteBuffer firstParams = null;
        while (true) {
            int type = in.readUnsignedByte();
            int flags = in.readUnsignedByte();
            in.readUnsignedShort(); // reserved
            int length = in.readInt();
            if (length < 0 || length > MAX_BODY) {
                throw new IOException("protocol error: message type " + type + " claims " + length + " bytes");
            }
            if (type == PONG && length == 24) {
                long t1 = in.readLong(), t2 = in.readLong(), t3 = in.readLong();
                pongs.add(new long[] {t1, t2, t3, System.nanoTime()});
                continue;
            }
            if (type == CURSOR_SHAPE && decoder != null) {
                readCursorShape(in, length);
                counts.cursorShapes++;
                continue;
            }
            if (type == CURSOR_POS && length == 16 && decoder != null) {
                int x = in.readInt(), y = in.readInt();
                long hubUs = in.readLong();
                counts.cursorPositions++;
                listener.onCursorPos((flags & FLAG_VISIBLE) != 0, x, y, hubUs);
                continue;
            }
            if (type != FRAME || length < FRAME_FIELDS || decoder == null) {
                // Unknown types are skipped by their length (DECISIONS 5.7),
                // and so is any FRAME on a camera connection, which has no decoder.
                if (skip.length < length) skip = new byte[length];
                in.readFully(skip, 0, length);
                counts.skipped++;
                continue;
            }

            Decoder.Unit u = new Decoder.Unit();
            u.seq = in.readInt() & 0xFFFFFFFFL;
            u.presentUs = in.readLong();
            u.encodedUs = in.readLong();
            u.sentUs = in.readLong();
            // Its own array: the unit sits in the decoder's queue while the
            // next one is read.
            byte[] data = new byte[length - FRAME_FIELDS];
            in.readFully(data);
            u.recvNs = System.nanoTime();
            counts.received++;
            if (counts.lastSeq >= 0) counts.seqGaps += u.seq - counts.lastSeq - 1;
            counts.lastSeq = u.seq;

            // DECISIONS 5.3, receiver side: nothing before SPS, PPS and an IDR.
            if (firstParams == null && flags != (FLAG_KEYFRAME | FLAG_CONFIG)) {
                counts.discardedBeforeKeyframe++;
                // Done with it, so the hub may send more (protocol 6).
                sendAck(u.seq);
                continue;
            }

            // Framing gives the boundaries: one message is one access unit,
            // checked over 4402 frames on 2026-10-11 by splitting each one
            // whole. That scan cost a median 3.3ms a frame, so now only the
            // NAL headers before the first slice are read, for SPS and PPS.
            long scanStart = System.nanoTime();
            u.au = new AnnexB.AccessUnit();
            u.au.data = ByteBuffer.wrap(data);
            u.au.idr = (flags & FLAG_KEYFRAME) != 0;
            u.au.params = new AnnexB(ByteBuffer.wrap(data)).leadingParams();
            counts.scanNs.add(System.nanoTime() - scanStart);
            if ((u.au.params != null) != ((flags & FLAG_CONFIG) != 0)) counts.flagMismatch++;
            if (u.au.params != null) {
                if (firstParams == null) {
                    firstParams = u.au.params;
                } else if (!firstParams.equals(u.au.params)) {
                    // A size change on the hub. Not handled in this slice.
                    counts.paramChanges++;
                    Log.w(Codecs.TAG, "link SPS/PPS changed mid-stream at seq " + u.seq + "; decoder not rebuilt");
                    firstParams = u.au.params;
                }
            }
            // The hub's present time: real, increasing, and it maps each
            // output back to its frame.
            u.ptsUs = u.presentUs;
            try {
                decoder.offer(u);
            } catch (InterruptedException e) {
                throw new IOException("interrupted");
            }
        }
    }
}
