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

import java.security.cert.X509Certificate;

import javax.net.ssl.SSLSocket;

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
    // ACCEPTED or DENIED. 9: the display sends PAUSE when it goes out of
    // view and RESUME when it shows again unchanged. 10: Wi-Fi is TLS, and
    // HELLO carries the pairing fields (Pairing). HELLO's layout is in the
    // hub's Wire.cs.
    static final int VERSION = 10, OLDEST = 7, TLS_FROM = 10;
    static final int PAUSES_FROM = 9;
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
    static final String HUB_TOO_OLD = "Exo on the PC is too old for this app. Update Exo on the PC.";
    private static final int CONNECT_TIMEOUT_MS = 3000, FIND_MS = 1000;
    static final int FRAME = 1, PING = 2, PONG = 3, FLAG_KEYFRAME = 1, FLAG_CONFIG = 2;
    static final int CURSOR_SHAPE = 4, CURSOR_POS = 5, TOUCH = 6, ACK = 7, PAUSE = 8, RESUME = 9, FLAG_VISIBLE = 1;
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

    // The display role's view of its connection, under view. paused: the
    // display is out of view (pause()), kept across connections, so one
    // made meanwhile starts paused. decoder: the one frames go to, null
    // while paused or between connections. displayName: the connection's,
    // null between them. spoken: the version the last accepted connection
    // speaks, 0 before one.
    private final Object view = new Object();
    private boolean paused;
    private Decoder decoder;
    private String displayName;
    private int resumes;
    private volatile int spoken;

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
                return hubVersion < oldest ? HUB_TOO_OLD
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
                if (e instanceof Reopen) continue;  // on purpose, so no wait
                if (e instanceof Refusal) {
                    refused = true;
                    status(e.getMessage());
                    break;
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

    /** The camera was ended on purpose (CameraRole.restart); connect again at once. */
    private static final class Reopen extends IOException {
        Reopen(String what) {
            super(what);
        }
    }

    /**
     * Found before HELLO and not fixed by trying again: a hub too old for
     * TLS, or not the PC this phone paired with. The message is the sentence.
     */
    private static final class Refusal extends IOException {
        Refusal(String sentence) {
            super(sentence);
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

        /** The PC's certificate over Wi-Fi; null over USB, which is plain. */
        default X509Certificate hub() {
            return null;
        }
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
                public void close() throws IOException {
                    // close() alone leaves a read blocked on the S21 until
                    // the hub's end closes too (cam/rot-fix-r2, 1s and more).
                    try {
                        ls.shutdownInput();
                    } catch (IOException ignored) {
                    }
                    ls.close();
                }
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
        // A hub from before TLS would close on the TLS handshake, and the
        // phone could only say it is looking.
        if (hub.protocol < TLS_FROM) throw new Refusal(HUB_TOO_OLD);
        Socket t = net.getSocketFactory().createSocket();
        Pairing.HubTrust trust = new Pairing.HubTrust(Pairing.pin(context, via.target()));
        SSLSocket[] tls = new SSLSocket[1];
        Conn c = new Conn() {
            public InputStream in() throws IOException { return tls[0].getInputStream(); }
            public OutputStream out() throws IOException { return tls[0].getOutputStream(); }
            // The TLS socket's own: its reads keep their own timeout.
            public void timeout(int ms) throws IOException { tls[0].setSoTimeout(ms); }
            public X509Certificate hub() { return trust.hub; }
            // The plain socket first, which wakes a blocked read or write, so
            // neither can keep the TLS socket's close waiting.
            public void close() throws IOException {
                try {
                    t.close();
                } finally {
                    if (tls[0] != null) tls[0].close();
                }
            }
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
            t.setSoTimeout(WELCOME_TIMEOUT_MS);
            tls[0] = Pairing.wrap(t, hub.address.getHostAddress(), hub.port, trust);
        } catch (IOException e) {
            c.close();
            if (trust.wrong) throw new Refusal(via.target() + " is not the PC this phone paired with. If Exo was"
                    + " installed on it again, pick the PC again to pair.");
            throw e;
        }
        Log.i(Codecs.TAG, String.format("link over Wi-Fi to \"%s\" at %s:%d, session %016x, %s %s", hub.name,
                hub.address.getHostAddress(), hub.port, hub.session, tls[0].getSession().getProtocol(),
                tls[0].getSession().getCipherSuite()));
        return c;
    }

    private void session() throws IOException {
        try (Conn s = open()) {
            if (s == null) return;
            long connectedNs = System.nanoTime();
            s.timeout(WELCOME_TIMEOUT_MS);
            DataInputStream in = new DataInputStream(new BufferedInputStream(s.in()));
            DataOutputStream out = new DataOutputStream(new BufferedOutputStream(s.out()));

            // Over Wi-Fi the PC is proven by its pin, or by the code: with no
            // pin this phone asks to pair, and goes on only once the code was
            // shown (Pairing).
            X509Certificate hubCert = s.hub();
            boolean pairs = hubCert != null && Pairing.pin(context, via.target()) == null;
            Pairing.Attempt attempt = hubCert != null ? Pairing.attempt(via.target()) : null;
            String code = null;

            byte[] myName = me.nameBytes();
            byte[] tail = listener.helloTail();
            int pairFields = version >= TLS_FROM ? 1 + Pairing.NONCE_SIZE : 0;
            out.write(MAGIC);
            out.writeShort(version);
            out.writeShort(2 + 1 + Identity.ID_SIZE + 1 + myName.length + 1 + tail.length + pairFields);
            out.writeShort(oldest);
            out.writeByte(role);
            out.write(me.id);
            out.writeByte(myName.length);
            out.write(myName);
            out.writeByte(listener.rotation());
            out.write(tail);
            if (pairFields > 0) {
                out.writeByte(pairs ? 1 : 0);
                out.write(attempt != null ? attempt.commitment : new byte[Pairing.NONCE_SIZE]);
            }
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
            // Over Wi-Fi the PC may ask its user first, after both sides show
            // the code; the answer is the next WELCOME. Waiting longer than
            // ASK_MS gives up and asks again, which joins the same question
            // on the PC, with the same code.
            if (status == PENDING) {
                if (attempt == null || body.length < Pairing.NONCE_SIZE) {
                    throw new IOException("PENDING without pairing, " + body.length + " bytes");
                }
                byte[] hubNonce = java.util.Arrays.copyOf(body, Pairing.NONCE_SIZE);
                // Stops rather than starting over: a stranger posing as the
                // PC would otherwise get a new try at the code each time.
                if (!attempt.hubNonce(hubNonce)) {
                    Pairing.end(attempt);
                    throw new Refusal("Pairing with " + via.target() + " started over on the PC. Start again to pair.");
                }
                out.write(attempt.nonce);
                out.flush();
                String pc = new String(body, Pairing.NONCE_SIZE, body.length - Pairing.NONCE_SIZE, StandardCharsets.UTF_8);
                try {
                    code = Pairing.code(hubCert, Pairing.certificate(), attempt.nonce, hubNonce);
                } catch (java.security.cert.CertificateException e) {
                    throw new IOException(e);
                }
                Log.i(Codecs.TAG, "link pending: " + pc + " is asking its user, code " + code);
                status("Allow this phone on " + pc + ": Exo asks there. Allow it only if the PC shows " + code + ".");
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
                if (status == DENIED) Pairing.end(attempt);
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
            if (pairs && code == null) throw new IOException("the PC accepted without pairing");
            if (code != null) {
                try {
                    Pairing.pin(context, via.target(), Pairing.fingerprint(hubCert));
                } catch (java.security.cert.CertificateException e) {
                    throw new IOException(e);
                }
                Pairing.end(attempt);
                Log.i(Codecs.TAG, "link paired with " + via.target());
            }
            Log.i(Codecs.TAG, String.format("link accepted after %d attempts, WELCOME in %.1fms, protocol %d,"
                    + " session %016x, identity %s \"%s\"", attempts, welcomeMs, hubVersion,
                    ByteBuffer.wrap(body).getLong(), Identity.hex(me.id), me.name));
            attempts = 0;
            lastFailure = null;
            spoken = hubVersion;
            status(null);

            // Nothing at all for this long and the connection is dead. PONGs
            // arrive every second, so a static screen does not trip it.
            s.timeout(SILENCE_MS);
            String name = "link-" + (++connections);
            if (role == ROLE_CAMERA) {
                cameraSession(s, in, out, name);
                return;
            }
            Counts counts = new Counts();
            ArrayList<long[]> pongs = new ArrayList<>();
            ScheduledExecutorService pinger = Executors.newSingleThreadScheduledExecutor();
            pinger.scheduleAtFixedRate(() -> ping(out), 0, PING_MS, TimeUnit.MILLISECONDS);
            upward = new Upward(pinger, out);
            Decoder first;
            synchronized (view) {
                displayName = name;
                resumes = 0;
                first = decoder = paused ? null : listener.newDecoder(name, this::sendAck);
                if (paused) {
                    Log.i(Codecs.TAG, "link connection " + connections + " starts paused");
                    sendUp(PAUSE, new byte[0]);
                }
            }
            if (first != null) first.start();
            try {
                readMessages(in, true, counts, pongs);
            } finally {
                upward = null;
                pinger.shutdownNow();
                // The next connection replays the shape and position.
                listener.onCursorPos(false, 0, 0, 0);
                Log.i(Codecs.TAG, "link connection " + connections + " ended: " + counts + ", pongs " + pongs.size());
                Decoder last;
                synchronized (view) {
                    last = decoder;
                    decoder = null;
                    displayName = null;
                }
                if (last != null) last.stop();
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
                readMessages(in, false, new Counts(), pongs);
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
                if (camera.failure() != null) {
                    String why = "camera: " + camera.failure();
                    throw camera.reopening() ? new Reopen(why) : new IOException(why);
                }
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

    /**
     * The display went out of view: its decoder lets go of the Surface
     * before this returns, the hub is told to pause, and the connection,
     * and so the PC's monitor, stays. False, changing nothing, before any
     * connection was accepted or with a hub below protocol 9; then stop the
     * link instead. UI thread.
     */
    boolean pause() {
        Decoder d;
        synchronized (view) {
            if (spoken < PAUSES_FROM) return false;
            if (paused) return true;
            paused = true;
            d = decoder;
            decoder = null;
            sendUp(PAUSE, new byte[0]);
        }
        Log.i(Codecs.TAG, "link paused" + (displayName == null ? ", between connections" : ""));
        if (d != null) d.stop();
        return true;
    }

    /** Whether pause() would keep the connection. */
    boolean canPause() {
        return spoken >= PAUSES_FROM;
    }

    boolean paused() {
        synchronized (view) {
            return paused;
        }
    }

    /**
     * Back in view with the same mode: a new decoder, on the Surface the
     * listener hands it now, and the hub sends a keyframe. With no
     * connection at the moment, the next one starts unpaused. False when
     * the connection now up speaks below protocol 9, made while paused with
     * a hub since replaced by an older one: it never sends the keyframe, so
     * connect again instead. UI thread.
     */
    boolean resume() {
        Decoder d;
        synchronized (view) {
            if (!paused) return true;
            if (displayName != null && spoken < PAUSES_FROM) return false;
            paused = false;
            if (displayName == null) {
                Log.i(Codecs.TAG, "link resumed, between connections");
                return true;
            }
            d = decoder = listener.newDecoder(displayName + "-resume" + (++resumes), this::sendAck);
            sendUp(RESUME, new byte[0]);
        }
        Log.i(Codecs.TAG, "link resumed");
        d.start();
        return true;
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
        int received, discardedBeforeKeyframe, droppedPaused, flagMismatch, paramChanges, skipped;
        int cursorShapes, cursorPositions;
        long seqGaps, lastSeq = -1;
        final ArrayList<Long> scanNs = new ArrayList<>();

        @Override
        public String toString() {
            return String.format("received %d, discarded before keyframe %d, dropped while paused %d, seq gaps %d,"
                            + " CONFIG flag disagreeing with the bytes %d, SPS/PPS changes %d,"
                            + " other messages skipped %d, cursor shapes %d, cursor positions %d", received,
                    discardedBeforeKeyframe, droppedPaused, seqGaps, flagMismatch, paramChanges, skipped, cursorShapes,
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

    /**
     * display: the display connection, whose frames go to the decoder of
     * the moment, or are acked and dropped while paused; else the camera's,
     * which only takes PONGs.
     */
    private void readMessages(DataInputStream in, boolean display, Counts counts, ArrayList<long[]> pongs)
            throws IOException {
        byte[] skip = new byte[256];
        ByteBuffer firstParams = null;
        Decoder fed = null;  // the decoder firstParams belongs to
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
            if (type == CURSOR_SHAPE && display) {
                readCursorShape(in, length);
                counts.cursorShapes++;
                continue;
            }
            if (type == CURSOR_POS && length == 16 && display) {
                int x = in.readInt(), y = in.readInt();
                long hubUs = in.readLong();
                counts.cursorPositions++;
                listener.onCursorPos((flags & FLAG_VISIBLE) != 0, x, y, hubUs);
                continue;
            }
            if (type != FRAME || length < FRAME_FIELDS || !display) {
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

            Decoder dec;
            synchronized (view) {
                dec = decoder;
            }
            if (dec != fed) {
                // Paused, or a new decoder after RESUME, which starts at a keyframe.
                fed = dec;
                firstParams = null;
            }
            if (dec == null) {
                counts.droppedPaused++;
                sendAck(u.seq);
                continue;
            }

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
                dec.offer(u);
            } catch (IOException e) {
                // A decoder pause() stopped meanwhile drops the frame; any
                // other's failure ends the connection.
                synchronized (view) {
                    if (decoder == dec) throw e;
                }
                sendAck(u.seq);
            } catch (InterruptedException e) {
                throw new IOException("interrupted");
            }
        }
    }
}
