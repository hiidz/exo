package exo.phone;

import android.graphics.Bitmap;
import android.net.LocalSocket;
import android.net.LocalSocketAddress;
import android.util.Log;

import java.io.BufferedInputStream;
import java.io.BufferedOutputStream;
import java.io.DataInputStream;
import java.io.DataOutputStream;
import java.io.File;
import java.io.IOException;
import java.io.PrintWriter;
import java.nio.ByteBuffer;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.concurrent.Executors;
import java.util.concurrent.ScheduledExecutorService;
import java.util.concurrent.TimeUnit;

/**
 * A connection to the hub: localabstract:exo, which adb reverse carries to
 * the hub's port. Connects, runs the handshake in DECISIONS 5.7, then serves
 * its role until the connection ends, and starts again a second later,
 * forever. The one exception is a version refusal, which does not fix
 * itself, so the loop stops and says so on screen.
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
        /** The hub refused, or this end refused the hub. Any thread. */
        void onRefused(String reason);

        /** A decoder for one accepted connection, not yet started. name tells runs apart. */
        Decoder newDecoder(String name);

        /** The camera for one accepted connection, not yet started. */
        CameraRole newCamera(String name);

        /** Where to write a per-connection log file called name. */
        File file(String name);

        /**
         * The hub's pointer shape (DECISIONS 11.1). hotX and hotY are the
         * hotspot within the bitmap. Link thread. A default method: the
         * camera role has no cursor and need not implement it.
         */
        default void onCursorShape(Bitmap shape, int hotX, int hotY) {
        }

        /** The hotspot in picture pixels, or hidden. Link thread. */
        default void onCursorPos(boolean visible, int x, int y) {
        }
    }

    static final byte[] MAGIC = {'E', 'X', 'O', 0};
    // 2 added CURSOR_SHAPE, CURSOR_POS and TOUCH on the display connection.
    static final int VERSION = 2;
    static final int ROLE_CAMERA = 1, ROLE_DISPLAY = 2, UP = 1, DOWN = 2;
    static final int ACCEPTED = 0, VERSION_MISMATCH = 1;
    static final int FRAME = 1, PING = 2, PONG = 3, FLAG_KEYFRAME = 1, FLAG_CONFIG = 2;
    static final int CURSOR_SHAPE = 4, CURSOR_POS = 5, TOUCH = 6, FLAG_VISIBLE = 1;
    private static final int PING_MS = 1000, SILENCE_MS = 3000;
    static final int FRAME_FIELDS = 4 + 3 * 8; // seq, present, encoded, sent
    static final int HEADER_SIZE = 8, MAX_BODY = 4 << 20;
    private static final int WELCOME_TIMEOUT_MS = 2000, RETRY_MS = 1000;

    private final String serial;
    private final int version;
    private final int role;
    private final Listener listener;
    private final Thread thread = new Thread(this, "link");
    private volatile boolean stopped;
    private volatile LocalSocket socket;
    private boolean refused;
    private int attempts;
    private String lastFailure;
    private int connections;

    /** version is VERSION except to prove a mismatch is refused. role is ROLE_CAMERA or ROLE_DISPLAY. */
    Link(String serial, int version, int role, Listener listener) {
        this.serial = serial == null ? "" : serial;
        this.version = version;
        this.role = role;
        this.listener = listener;
    }

    void start() {
        thread.start();
    }

    void stop() {
        stopped = true;
        LocalSocket s = socket;
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
                    Log.i(Codecs.TAG, "link attempt " + attempts + ": " + what);
                    lastFailure = what;
                }
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

    private void session() throws IOException {
        try (LocalSocket s = new LocalSocket()) {
            socket = s;
            if (stopped) return;
            s.connect(new LocalSocketAddress("exo", LocalSocketAddress.Namespace.ABSTRACT));
            long connectedNs = System.nanoTime();
            s.setSoTimeout(WELCOME_TIMEOUT_MS);
            DataInputStream in = new DataInputStream(new BufferedInputStream(s.getInputStream()));
            DataOutputStream out = new DataOutputStream(new BufferedOutputStream(s.getOutputStream()));

            byte[] id = serial.getBytes(StandardCharsets.US_ASCII);
            out.write(MAGIC);
            out.writeShort(version);
            out.writeShort(3 + id.length);
            out.writeByte(role);
            out.writeByte(role == ROLE_CAMERA ? UP : DOWN);
            out.writeByte(id.length);
            out.write(id);
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
            double welcomeMs = (System.nanoTime() - connectedNs) / 1e6;

            if (status != ACCEPTED) {
                String reason = new String(body, StandardCharsets.UTF_8);
                Log.e(Codecs.TAG, "link REFUSED by hub, status " + status + ", hub protocol " + hubVersion
                        + ", ours " + version + ": " + reason);
                if (status == VERSION_MISMATCH) {
                    refused = true;
                    listener.onRefused("Refused by the hub: " + reason);
                }
                return;
            }
            if (hubVersion != version) {
                String reason = "hub accepted at protocol " + hubVersion + ", this app speaks " + version;
                Log.e(Codecs.TAG, "link REFUSED the hub: " + reason);
                refused = true;
                listener.onRefused("Refused the hub: " + reason);
                return;
            }
            Log.i(Codecs.TAG, String.format("link accepted after %d attempts, WELCOME in %.1fms, protocol %d,"
                    + " session %016x, serial \"%s\"", attempts, welcomeMs, hubVersion,
                    ByteBuffer.wrap(body).getLong(), serial));
            attempts = 0;
            lastFailure = null;

            // Nothing at all for this long and the connection is dead. PONGs
            // arrive every second, so a static screen does not trip it.
            s.setSoTimeout(SILENCE_MS);
            String name = "link-" + (++connections);
            if (role == ROLE_CAMERA) {
                cameraSession(s, in, out, name);
                return;
            }
            Decoder decoder = listener.newDecoder(name);
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
                listener.onCursorPos(false, 0, 0);
                Log.i(Codecs.TAG, "link connection " + connections + " ended: " + counts + ", pongs " + pongs.size());
                decoder.stop();
                writePongs(listener.file(name + "-pongs.csv"), pongs);
            }
        }
    }

    /**
     * The camera role's connection. This thread writes the camera's frames
     * up; a reader thread takes the PONGs, and its silence timeout or a read
     * error closes the socket, which ends the writing too. A camera failure
     * ends the connection, and the next one starts a new camera.
     */
    private void cameraSession(LocalSocket s, DataInputStream in, DataOutputStream out, String name)
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
        Upward u = upward;
        if (u == null) return false;
        try {
            u.writer.execute(() -> {
                try {
                    synchronized (u.out) {
                        u.out.writeByte(TOUCH);
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
                in.readLong(); // the hub's mouse update time, unused yet
                counts.cursorPositions++;
                listener.onCursorPos((flags & FLAG_VISIBLE) != 0, x, y);
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
