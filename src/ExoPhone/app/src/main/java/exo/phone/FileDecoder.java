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
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;

/**
 * Decodes an Annex-B file to a Surface and measures every frame. All codec
 * calls and callbacks run on one HandlerThread, so nothing here is locked.
 */
final class FileDecoder {
    interface SizeListener {
        /** The decoded picture's display size, from the decoder. */
        void onSize(int width, int height);
    }

    /** What happened to one access unit. Times are System.nanoTime. */
    private static final class Frame {
        long queueNs, outNs = -1;
        int bytes, inFlightAtOut;
        boolean idr;
    }

    private final File file, csv;
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
    private AnnexB.AccessUnit pending;
    private final ArrayDeque<Integer> freeInputs = new ArrayDeque<>();
    private final ArrayList<Frame> frames = new ArrayList<>();
    private final Runnable feedLater = this::feed;
    private boolean configSent, inputDone, finished;
    private int skipped, outputs, released, renderCallbacks, firstOutputInFlight = -1, inputCapacity;
    private long t0Ms;

    FileDecoder(File file, File csv, double fps, int hintWidth, int hintHeight,
                boolean paced, boolean lowLatency, String codecName, Surface surface, SizeListener sizeListener) {
        this.file = file;
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
        thread.start();
        handler = new Handler(thread.getLooper());
        handler.post(this::open);
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
        try (FileChannel ch = FileChannel.open(file.toPath(), StandardOpenOption.READ)) {
            stream = new AnnexB(ch.map(FileChannel.MapMode.READ_ONLY, 0, ch.size()));
        } catch (IOException e) {
            Log.e(Codecs.TAG, "cannot open " + file, e);
            return;
        }
        // DECISIONS 5.3: discard everything before codec config and an IDR.
        while ((pending = stream.next()) != null && !(pending.idr && pending.params != null)) skipped++;
        if (pending == null) {
            Log.e(Codecs.TAG, "no SPS/PPS with an IDR in " + file);
            return;
        }

        MediaFormat format = MediaFormat.createVideoFormat(Codecs.AVC, hintWidth, hintHeight);
        if (lowLatency && Build.VERSION.SDK_INT >= 30) format.setInteger(MediaFormat.KEY_LOW_LATENCY, 1);
        try {
            // KEY_LOW_LATENCY does not steer findDecoderForFormat: on the
            // TB375FC it picks c2.mtk.avc.decoder over the separate
            // c2.mtk.avc.decoder.lowlatency. A name, when given, wins.
            if (codecName == null) {
                codecName = new MediaCodecList(MediaCodecList.REGULAR_CODECS).findDecoderForFormat(format);
            }
            codec = MediaCodec.createByCodecName(codecName);
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
                handler.postDelayed(FileDecoder.this::finish, 500);
                return;
            }
            Frame f = frameAt(info.presentationTimeUs);
            if (f != null) {
                f.outNs = System.nanoTime();
                f.inFlightAtOut = frames.size() - outputs;
                if (firstOutputInFlight < 0) firstOutputInFlight = f.inFlightAtOut;
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
        while (!inputDone && !freeInputs.isEmpty()) {
            if (!configSent) {
                queue(pending.params, 0, MediaCodec.BUFFER_FLAG_CODEC_CONFIG);
                configSent = true;
                continue;
            }
            if (pending == null) {
                codec.queueInputBuffer(freeInputs.poll(), 0, 0, 0, MediaCodec.BUFFER_FLAG_END_OF_STREAM);
                inputDone = true;
                return;
            }
            int i = frames.size();
            if (paced) {
                long due = t0Ms + Math.round(i * 1000 / fps);
                if (SystemClock.uptimeMillis() < due) {
                    handler.removeCallbacks(feedLater);
                    handler.postAtTime(feedLater, due);
                    return;
                }
            }
            Frame f = new Frame();
            f.bytes = pending.data.remaining();
            f.idr = pending.idr;
            frames.add(f);
            f.queueNs = System.nanoTime();
            queue(pending.data, ptsOf(i), 0);
            pending = stream.next();
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

    private Frame frameAt(long ptsUs) {
        int i = (int) Math.round(ptsUs * fps / 1e6);
        return i >= 0 && i < frames.size() ? frames.get(i) : null;
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
        long[] q2o = new long[n];
        int nq = 0, maxInFlight = 0;
        for (Frame f : frames) {
            if (f.outNs >= 0) q2o[nq++] = f.outNs - f.queueNs;
            maxInFlight = Math.max(maxInFlight, f.inFlightAtOut);
        }
        long wallNs = n > 1 ? frames.get(n - 1).queueNs - frames.get(0).queueNs : 0;
        Log.i(Codecs.TAG, String.format("done %s %s fps=%.0f %s lowLatency=%b: skipped %d, queued %d, output %d,"
                        + " released %d, render callbacks %d, decoder dropped %d, input rate %.1f/s",
                file.getName(), codecName, fps, paced ? "paced" : "fast", lowLatency, skipped, n, outputs,
                released, renderCallbacks, n - outputs, wallNs > 0 ? (n - 1) * 1e9 / wallNs : 0));
        Log.i(Codecs.TAG, "  queued when first output arrived " + firstOutputInFlight
                + ", max in flight " + maxInFlight);
        Log.i(Codecs.TAG, "  queue->output ms " + stats(q2o, nq));

        try (PrintWriter w = new PrintWriter(csv)) {
            w.println("frame,pts_us,bytes,idr,queue_ms,q2o_ms,in_flight_at_out");
            long base = n > 0 ? frames.get(0).queueNs : 0;
            for (int i = 0; i < n; i++) {
                Frame f = frames.get(i);
                w.printf("%d,%d,%d,%d,%.3f,%s,%d%n", i, ptsOf(i), f.bytes, f.idr ? 1 : 0,
                        (f.queueNs - base) / 1e6,
                        f.outNs < 0 ? "" : String.format("%.3f", (f.outNs - f.queueNs) / 1e6),
                        f.inFlightAtOut);
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
