package exo.phone;

import android.media.MediaCodec;
import android.media.MediaCodecList;
import android.media.MediaFormat;
import android.os.Build;
import android.os.Handler;
import android.os.HandlerThread;
import android.os.SystemClock;
import android.util.Log;
import android.view.Surface;

import java.io.File;
import java.io.IOException;
import java.io.PrintWriter;
import java.nio.ByteBuffer;
import java.nio.channels.FileChannel;
import java.nio.file.StandardOpenOption;
import java.util.ArrayDeque;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.HashMap;
import java.util.concurrent.ArrayBlockingQueue;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;

/**
 * Decodes access units to a Surface and measures every frame. They come
 * from an Annex-B file, or from the hub through Link (DECISIONS 5.7). All
 * codec calls and callbacks run on one HandlerThread, so nothing here is
 * locked; the one thing shared with another thread is the live queue.
 */
final class Decoder {
    interface SizeListener {
        /** The decoded picture's display size, from the decoder. */
        void onSize(int width, int height);
    }

    /** One access unit to decode, and what is known about it. */
    static final class Unit {
        AnnexB.AccessUnit au;
        long ptsUs;
        // From the link only; -1 from a file. Hub times are microseconds on
        // the hub's clock; recvNs is System.nanoTime when the last byte came.
        long seq = -1, presentUs = -1, encodedUs = -1, sentUs = -1, recvNs = -1;
    }

    /** What happened to one access unit. Times are System.nanoTime. */
    private static final class Frame {
        final Unit u;
        long queueNs, outNs = -1;
        int bytes;
        boolean idr;
        int inFlightAtOut;

        Frame(Unit u) {
            this.u = u;
        }
    }

    // Exactly one of file and live is set.
    private final File file, csv;
    private final ArrayBlockingQueue<Unit> live;
    private final double fps;
    private final int hintWidth, hintHeight;
    private final boolean paced, lowLatency;
    private final Surface surface;
    private final SizeListener sizeListener;

    private final HandlerThread thread = new HandlerThread("decode");
    private Handler handler;
    private MediaCodec codec;
    private String codecName;
    private AnnexB stream;
    private int fileUnits;
    private Unit pending;
    private final ArrayDeque<Integer> freeInputs = new ArrayDeque<>();
    private final ArrayList<Frame> frames = new ArrayList<>();
    private final HashMap<Long, Frame> byPts = new HashMap<>();
    private final Runnable feedLater = this::feed;
    private boolean configSent, inputDone;
    private volatile boolean finished; // read by offer() on the reader thread
    private int skipped, outputs, released, renderCallbacks, firstOutputInFlight = -1, inputCapacity;
    private long t0Ms, startNs;

    /** From a file, paced at fps or as fast as the decoder takes it. */
    static Decoder ofFile(File file, File csv, double fps, int hintWidth, int hintHeight, boolean paced,
                          boolean lowLatency, String codecName, Surface surface, SizeListener sizeListener) {
        return new Decoder(file, null, csv, fps, hintWidth, hintHeight, paced, lowLatency, codecName, surface,
                sizeListener);
    }

    /**
     * From the link. The first unit offered must carry SPS and PPS with an
     * IDR; Link discards everything before one (DECISIONS 5.3).
     */
    static Decoder ofLink(File csv, int hintWidth, int hintHeight, int fps, boolean lowLatency, String codecName,
                          Surface surface, SizeListener sizeListener) {
        return new Decoder(null, new ArrayBlockingQueue<>(2), csv, fps, hintWidth, hintHeight, false, lowLatency,
                codecName, surface, sizeListener);
    }

    private Decoder(File file, ArrayBlockingQueue<Unit> live, File csv, double fps, int hintWidth, int hintHeight,
                    boolean paced, boolean lowLatency, String codecName, Surface surface, SizeListener sizeListener) {
        this.file = file;
        this.live = live;
        this.codecName = codecName;
        this.csv = csv;
        this.fps = fps;
        this.hintWidth = hintWidth;
        this.hintHeight = hintHeight;
        this.paced = paced;
        this.lowLatency = lowLatency;
        this.surface = surface;
        this.sizeListener = sizeListener;
    }

    void start() {
        startNs = System.nanoTime();
        thread.start();
        handler = new Handler(thread.getLooper());
        handler.post(this::open);
    }

    /**
     * Called by Link's reader thread. Blocks while two units are already
     * waiting, so a slow decoder slows the socket instead of growing a
     * queue (DECISIONS 5.7).
     */
    void offer(Unit u) throws InterruptedException, IOException {
        // Waits in slices so a decoder that has died, which will never take
        // another unit, cannot block the reader forever.
        while (!live.offer(u, 100, TimeUnit.MILLISECONDS)) {
            if (finished) throw new IOException("decoder finished");
        }
        handler.post(feedLater);
    }

    /** Blocks until the codec has let go of the Surface. */
    void stop() {
        CountDownLatch done = new CountDownLatch(1);
        if (!handler.post(() -> { finish(); done.countDown(); })) return; // thread already gone
        try {
            done.await(2, TimeUnit.SECONDS);
        } catch (InterruptedException e) {
            Thread.currentThread().interrupt();
        }
    }

    private void open() {
        if (file != null) {
            try (FileChannel ch = FileChannel.open(file.toPath(), StandardOpenOption.READ)) {
                stream = new AnnexB(ch.map(FileChannel.MapMode.READ_ONLY, 0, ch.size()));
            } catch (IOException e) {
                Log.e(Codecs.TAG, "cannot open " + file, e);
                return;
            }
            // DECISIONS 5.3: discard everything before codec config and an IDR.
            while ((pending = fromFile()) != null && !(pending.au.idr && pending.au.params != null)) skipped++;
            if (pending == null) {
                Log.e(Codecs.TAG, "no SPS/PPS with an IDR in " + file);
                return;
            }
        }

        // The same format and choice DisplaySettings checked its modes with.
        MediaFormat format = Codecs.decoderFormat(hintWidth, hintHeight, (int) Math.round(fps), lowLatency);
        try {
            String chosen = Codecs.chooseDecoder(format, codecName);
            codec = MediaCodec.createByCodecName(chosen != null ? chosen : codecName);
            codecName = codec.getName();
        } catch (IOException | NullPointerException e) {
            Log.e(Codecs.TAG, "no decoder for " + format, e);
            return;
        }
        codec.setCallback(callback, handler);
        // Counted only. Measured on the SM-G991B: the callback's time is the
        // frame's own timestamp echoed back, and it fired for all 972 frames
        // of a run where SurfaceFlinger dropped 76. Rendered and dropped
        // come from dumpsys SurfaceFlinger --timestats instead.
        codec.setOnFrameRenderedListener((c, ptsUs, nanoTime) -> renderCallbacks++, handler);
        codec.configure(format, surface, null, 0);
        Log.i(Codecs.TAG, "configured " + codecName + " with " + format);
        Log.i(Codecs.TAG, "input format " + codec.getInputFormat());
        codec.start();
        t0Ms = SystemClock.uptimeMillis();
    }

    private Unit fromFile() {
        AnnexB.AccessUnit au = stream.next();
        if (au == null) return null;
        Unit u = new Unit();
        u.au = au;
        u.ptsUs = ptsOf(fileUnits++);
        return u;
    }

    private final MediaCodec.Callback callback = new MediaCodec.Callback() {
        @Override
        public void onInputBufferAvailable(MediaCodec c, int index) {
            freeInputs.add(index);
            feed();
        }

        @Override
        public void onOutputBufferAvailable(MediaCodec c, int index, MediaCodec.BufferInfo info) {
            if ((info.flags & MediaCodec.BUFFER_FLAG_END_OF_STREAM) != 0) {
                c.releaseOutputBuffer(index, false);
                // Render callbacks for the last frames can still be on their
                // way, and have no completion signal.
                handler.postDelayed(Decoder.this::finish, 500);
                return;
            }
            Frame f = byPts.remove(info.presentationTimeUs);
            if (f != null) {
                f.outNs = System.nanoTime();
                f.inFlightAtOut = frames.size() - outputs;
                if (firstOutputInFlight < 0) {
                    firstOutputInFlight = f.inFlightAtOut;
                    // What a rejoin costs: from start(), which for the link is the
                    // moment WELCOME arrived.
                    Log.i(Codecs.TAG, String.format("first frame out %.1fms after start, %.1fms after queued",
                            (f.outNs - startNs) / 1e6, (f.outNs - f.queueNs) / 1e6));
                }
            }
            outputs++;
            // Render as soon as possible: for a live display the newest
            // frame is the right one.
            c.releaseOutputBuffer(index, true);
            released++;
        }

        @Override
        public void onOutputFormatChanged(MediaCodec c, MediaFormat format) {
            Log.i(Codecs.TAG, "output format " + format);
            int w = format.getInteger(MediaFormat.KEY_WIDTH), h = format.getInteger(MediaFormat.KEY_HEIGHT);
            if (format.containsKey("crop-right")) {
                w = format.getInteger("crop-right") - format.getInteger("crop-left") + 1;
                h = format.getInteger("crop-bottom") - format.getInteger("crop-top") + 1;
            }
            sizeListener.onSize(w, h);
        }

        @Override
        public void onError(MediaCodec c, MediaCodec.CodecException e) {
            Log.e(Codecs.TAG, "codec error " + e.getDiagnosticInfo(), e);
            finish();
        }
    };

    /** Queues as many access units as there are free buffers and due frames. */
    private void feed() {
        if (codec == null || finished) return;
        while (!inputDone && !freeInputs.isEmpty()) {
            if (pending == null) {
                pending = live != null ? live.poll() : fromFile();
                if (pending == null) {
                    if (live != null) return; // offer() wakes this when the next unit arrives
                    codec.queueInputBuffer(freeInputs.poll(), 0, 0, 0, MediaCodec.BUFFER_FLAG_END_OF_STREAM);
                    inputDone = true;
                    return;
                }
            }
            if (!configSent) {
                queue(pending.au.params, 0, MediaCodec.BUFFER_FLAG_CODEC_CONFIG);
                configSent = true;
                continue;
            }
            if (paced) {
                long due = t0Ms + Math.round(frames.size() * 1000 / fps);
                if (SystemClock.uptimeMillis() < due) {
                    handler.removeCallbacks(feedLater);
                    handler.postAtTime(feedLater, due);
                    return;
                }
            }
            Frame f = new Frame(pending);
            frames.add(f);
            byPts.put(pending.ptsUs, f);
            f.queueNs = System.nanoTime();
            f.bytes = pending.au.data.remaining();
            f.idr = pending.au.idr;
            queue(pending.au.data, pending.ptsUs, 0);
            // The record outlives the run; the bytes must not. 30 frames a
            // second at 160KB filled the 256MB heap in 53s.
            pending.au = null;
            pending = null;
        }
    }

    private void queue(ByteBuffer src, long ptsUs, int flags) {
        int index = freeInputs.poll();
        ByteBuffer in = codec.getInputBuffer(index);
        if (inputCapacity == 0) {
            inputCapacity = in.capacity();
            Log.i(Codecs.TAG, "input buffer capacity " + inputCapacity);
        }
        int size = src.remaining();
        in.clear();
        in.put(src.duplicate());
        codec.queueInputBuffer(index, 0, size, ptsUs, flags);
    }

    private long ptsOf(int i) {
        return Math.round(i * 1e6 / fps);
    }

    private void finish() {
        if (finished) return;
        finished = true;
        handler.removeCallbacks(feedLater);
        if (codec != null) {
            codec.stop();
            codec.release();
        }
        report();
        thread.quitSafely();
    }

    private void report() {
        int n = frames.size();
        long[] q2o = new long[n], r2q = new long[n];
        int nq = 0, nr = 0, maxInFlight = 0;
        for (Frame f : frames) {
            if (f.outNs >= 0) q2o[nq++] = f.outNs - f.queueNs;
            if (f.u.recvNs >= 0) r2q[nr++] = f.queueNs - f.u.recvNs;
            maxInFlight = Math.max(maxInFlight, f.inFlightAtOut);
        }
        long wallNs = n > 1 ? frames.get(n - 1).queueNs - frames.get(0).queueNs : 0;
        String source = file != null ? file.getName() + " fps=" + Math.round(fps) + (paced ? " paced" : " fast")
                : "link";
        Log.i(Codecs.TAG, String.format("done %s %s lowLatency=%b: skipped %d, queued %d, output %d,"
                        + " released %d, render callbacks %d, decoder dropped %d, input rate %.1f/s",
                source, codecName, lowLatency, skipped, n, outputs,
                released, renderCallbacks, n - outputs, wallNs > 0 ? (n - 1) * 1e9 / wallNs : 0));
        Log.i(Codecs.TAG, "  queued when first output arrived " + firstOutputInFlight
                + ", max in flight " + maxInFlight);
        Log.i(Codecs.TAG, "  queue->output ms " + stats(q2o, nq));
        if (nr > 0) Log.i(Codecs.TAG, "  received->queued ms " + stats(r2q, nr));

        // The link columns are raw clock readings, hub and phone, for the
        // offline analysis that relates the two clocks.
        try (PrintWriter w = new PrintWriter(csv)) {
            w.println("frame,pts_us,bytes,idr,queue_ms,q2o_ms,in_flight_at_out,"
                    + "seq,present_us,encoded_us,sent_us,recv_ns,queue_ns,out_ns");
            long base = n > 0 ? frames.get(0).queueNs : 0;
            for (int i = 0; i < n; i++) {
                Frame f = frames.get(i);
                Unit u = f.u;
                w.printf("%d,%d,%d,%d,%.3f,%s,%d,%d,%d,%d,%d,%d,%d,%d%n", i, u.ptsUs, f.bytes,
                        f.idr ? 1 : 0, (f.queueNs - base) / 1e6,
                        f.outNs < 0 ? "" : String.format("%.3f", (f.outNs - f.queueNs) / 1e6),
                        f.inFlightAtOut, u.seq, u.presentUs, u.encodedUs, u.sentUs, u.recvNs, f.queueNs, f.outNs);
            }
            Log.i(Codecs.TAG, "  wrote " + csv);
        } catch (IOException e) {
            Log.e(Codecs.TAG, "cannot write " + csv, e);
        }
    }

    private static String stats(long[] ns, int n) {
        if (n == 0) return "n=0";
        long[] s = Arrays.copyOf(ns, n);
        Arrays.sort(s);
        return String.format("n=%d min=%.2f median=%.2f p95=%.2f p99=%.2f max=%.2f", n,
                s[0] / 1e6, s[n / 2] / 1e6, s[(int) (n * 0.95)] / 1e6, s[(int) (n * 0.99)] / 1e6, s[n - 1] / 1e6);
    }
}
