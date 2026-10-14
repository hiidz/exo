package exo.phone;

import android.content.Context;
import android.hardware.camera2.CameraAccessException;
import android.hardware.camera2.CameraCaptureSession;
import android.hardware.camera2.CameraCharacteristics;
import android.hardware.camera2.CameraDevice;
import android.hardware.camera2.CameraManager;
import android.hardware.camera2.CaptureRequest;
import android.hardware.camera2.params.StreamConfigurationMap;
import android.hardware.display.DisplayManager;
import android.media.MediaCodec;
import android.media.MediaCodecInfo;
import android.media.MediaFormat;
import android.os.Build;
import android.os.Bundle;
import android.os.Handler;
import android.os.HandlerThread;
import android.util.Log;
import android.util.Range;
import android.util.Size;
import android.view.Display;
import android.view.Surface;

import java.io.IOException;
import java.nio.ByteBuffer;
import java.util.Arrays;
import java.util.Collections;
import java.util.concurrent.ArrayBlockingQueue;
import java.util.concurrent.TimeUnit;

/**
 * The camera role for one connection (DECISIONS 10 item 4, increment D): the
 * back camera into an H.264 encoder, and the encoded frames into a bounded
 * queue the link drains to the hub. A new one per connection, so every
 * connection starts with codec config and an IDR (DECISIONS 5.3).
 *
 * The camera draws straight into the encoder's input Surface, so no frame
 * passes through Java. Camera2 delivers its callbacks on a HandlerThread, a
 * thread with its own message loop. One plain thread drains the encoder.
 * Encoded frames cannot be dropped singly (5.7): when the queue is full the
 * drain discards up to the next keyframe and asks the encoder for one.
 */
final class CameraRole {
    static final int WIDTH = 1280, HEIGHT = 720, FPS = 30;
    // Spike 4's rate, which delivered 7.75 Mbps of 8.
    private static final int BITRATE = 8_000_000;
    private static final int QUEUE = 8;

    /** One encoded access unit, as the link sends it. */
    static final class Frame {
        byte[] data;
        int flags;           // Link.FLAG_KEYFRAME, Link.FLAG_CONFIG
        long presentUs;      // the encoder's presentation time: the capture timestamp
        long encodedUs;      // System.nanoTime() / 1000 when it left the encoder
    }

    private final Context context;
    private final CameraSettings settings;
    private final CameraSettings.Lens lens;
    private final ArrayBlockingQueue<Frame> queue = new ArrayBlockingQueue<>(QUEUE);
    private final HandlerThread cameraThread = new HandlerThread("camera");
    private final Thread drainThread = new Thread(this::drain, "encoder drain");
    private MediaCodec encoder;
    private Surface input;
    private Handler handler;                         // the camera thread's
    private CameraCharacteristics chars;
    private volatile CameraDevice camera;            // set on the camera thread
    private volatile CameraCaptureSession session;
    private CaptureRequest.Builder request;          // camera thread only
    private volatile boolean stopped;
    private volatile String failure;

    /** The lens and controls come from settings, as they are now. */
    CameraRole(Context context, CameraSettings settings) {
        this.context = context;
        this.settings = settings;
        this.lens = settings.lens();
    }

    /** The camera this one opens, to tell whether a settings change needs another. */
    String lensId() {
        return lens == null ? null : lens.id;
    }

    /**
     * Ends this camera on purpose, as a failure does: the connection ends,
     * and the next one opens the lens now chosen.
     */
    void restart(String why) {
        if (failure == null) {
            failure = why;
            Log.i(Codecs.TAG, "camera reopening: " + why);
        }
    }

    /** The zoom, focus, exposure and torch in s, into the running capture. Any thread. */
    void update(CameraSettings s) {
        Handler h = handler;
        if (h == null) return;
        h.post(() -> {
            if (session == null || request == null || stopped) return;
            controls(request, s);
            try {
                session.setRepeatingRequest(request.build(), null, h);
                Log.i(Codecs.TAG, "camera controls now " + s);
            } catch (CameraAccessException | IllegalStateException e) {
                Log.w(Codecs.TAG, "camera controls not applied: " + e);
            }
        });
    }

    /**
     * Zoom, focus, exposure and torch, each only as far as this lens can.
     * Zoom by ratio from Android 11; on 10 by cropping the active array.
     */
    private void controls(CaptureRequest.Builder b, CameraSettings s) {
        float zoom = Math.max(1, Math.min(lens.maxZoom, s.zoom));
        if (Build.VERSION.SDK_INT >= 30) {
            b.set(CaptureRequest.CONTROL_ZOOM_RATIO, zoom);
        } else {
            android.graphics.Rect a = chars.get(CameraCharacteristics.SENSOR_INFO_ACTIVE_ARRAY_SIZE);
            int w = Math.round(a.width() / zoom), h = Math.round(a.height() / zoom);
            b.set(CaptureRequest.SCALER_CROP_REGION, new android.graphics.Rect(
                    a.left + (a.width() - w) / 2, a.top + (a.height() - h) / 2,
                    a.left + (a.width() + w) / 2, a.top + (a.height() + h) / 2));
        }
        if (s.focus > 0 && lens.minFocus > 0) {
            b.set(CaptureRequest.CONTROL_AF_MODE, CaptureRequest.CONTROL_AF_MODE_OFF);
            b.set(CaptureRequest.LENS_FOCUS_DISTANCE, Math.min(1, s.focus) * lens.minFocus);
        } else if (lens.minFocus > 0) {
            b.set(CaptureRequest.CONTROL_AF_MODE, CaptureRequest.CONTROL_AF_MODE_CONTINUOUS_VIDEO);
        }
        b.set(CaptureRequest.CONTROL_AE_EXPOSURE_COMPENSATION, Math.max(lens.aeMin, Math.min(lens.aeMax, s.exposure)));
        b.set(CaptureRequest.FLASH_MODE, s.torch && lens.flash ? CaptureRequest.FLASH_MODE_TORCH
                : CaptureRequest.FLASH_MODE_OFF);
    }

    /** Why the camera or encoder failed, or null. The link ends the connection on it. */
    String failure() {
        return failure;
    }

    /** The next frame, waiting up to timeoutMs; null if none came. */
    Frame take(long timeoutMs) throws InterruptedException {
        return queue.poll(timeoutMs, TimeUnit.MILLISECONDS);
    }

    void start() throws IOException {
        // CameraSettings lists only lenses that can send WIDTH x HEIGHT to an encoder.
        if (lens == null) throw new IOException("no camera on this phone can be the webcam");
        CameraManager cm = context.getSystemService(CameraManager.class);
        String id = lens.id;
        CameraCharacteristics c;
        try {
            c = cm.getCameraCharacteristics(id);
        } catch (CameraAccessException e) {
            throw new IOException("camera characteristics: " + e, e);
        }
        chars = c;
        Range<Integer>[] ranges = c.get(CameraCharacteristics.CONTROL_AE_AVAILABLE_TARGET_FPS_RANGES);
        Range<Integer> fps = chooseFps(ranges);
        Log.i(Codecs.TAG, "camera " + id + (lens.front ? " front" : " back") + ", " + settings
                + ", sensorOrientation " + c.get(CameraCharacteristics.SENSOR_ORIENTATION)
                + ", timestamp source " + c.get(CameraCharacteristics.SENSOR_INFO_TIMESTAMP_SOURCE)
                + " (0 unknown, 1 realtime), fps ranges " + Arrays.toString(ranges) + ", chosen " + fps);
        // Spike 4's order: configure, then createInputSurface, then start.
        MediaFormat f = MediaFormat.createVideoFormat(Codecs.AVC, WIDTH, HEIGHT);
        f.setInteger(MediaFormat.KEY_COLOR_FORMAT, MediaCodecInfo.CodecCapabilities.COLOR_FormatSurface);
        f.setInteger(MediaFormat.KEY_BIT_RATE, BITRATE);
        f.setInteger(MediaFormat.KEY_FRAME_RATE, FPS);
        f.setInteger(MediaFormat.KEY_I_FRAME_INTERVAL, 1);
        // An encoder key: frames out as they are encoded, not batched (traps-phone).
        if (Build.VERSION.SDK_INT >= 30) f.setInteger(MediaFormat.KEY_LATENCY, 1);
        encoder = MediaCodec.createEncoderByType(Codecs.AVC);
        encoder.configure(f, null, null, MediaCodec.CONFIGURE_FLAG_ENCODE);
        input = encoder.createInputSurface();
        encoder.start();
        Log.i(Codecs.TAG, "encoder " + encoder.getName() + " " + f);
        drainThread.start();

        cameraThread.start();
        Handler h = new Handler(cameraThread.getLooper());
        handler = h;
        try {
            cm.openCamera(id, new CameraDevice.StateCallback() {
                @Override
                public void onOpened(CameraDevice d) {
                    camera = d;
                    configure(d, fps, h);
                }

                @Override
                public void onDisconnected(CameraDevice d) {
                    fail("camera disconnected");
                    d.close();
                }

                @Override
                public void onError(CameraDevice d, int error) {
                    fail("camera error " + error);
                    d.close();
                }
            }, h);
        } catch (CameraAccessException | SecurityException e) {
            throw new IOException("openCamera: " + e, e);
        }
    }

    /**
     * The clockwise quarter turns the hub must give the picture to show it
     * upright, as the device is held now (DECISIONS 6.4), for the lens in
     * settings. A front camera turns the other way; its picture needs no
     * mirroring, since Camera2 gives it as others see you. Read at each
     * connect, so a phone turned over between connections is caught at the
     * next one.
     */
    static int rotation(Context context, CameraSettings settings) throws IOException {
        CameraSettings.Lens lens = settings.lens();
        if (lens == null) return 0;
        int sensor;
        try {
            sensor = context.getSystemService(CameraManager.class).getCameraCharacteristics(lens.id)
                    .get(CameraCharacteristics.SENSOR_ORIENTATION);
        } catch (CameraAccessException e) {
            throw new IOException("camera characteristics: " + e, e);
        }
        int display = context.getSystemService(DisplayManager.class).getDisplay(Display.DEFAULT_DISPLAY)
                .getRotation() * 90;
        int turns = (lens.front ? sensor + display : sensor - display + 360) % 360 / 90;
        Log.i(Codecs.TAG, "camera rotation: lens " + lens.id + (lens.front ? " front" : " back")
                + ", sensorOrientation " + sensor + ", display " + display + " degrees, hub turns " + turns * 90);
        return turns;
    }

    /** A fixed 30 if offered; else the range topping out at 30 with the highest floor (DECISIONS 6.4). */
    private static Range<Integer> chooseFps(Range<Integer>[] ranges) {
        Range<Integer> best = null;
        for (Range<Integer> r : ranges) {
            if (r.getUpper() != FPS) continue;
            if (best == null || r.getLower() > best.getLower()) best = r;
        }
        return best;
    }

    /** On the camera thread. */
    private void configure(CameraDevice d, Range<Integer> fps, Handler h) {
        try {
            // createCaptureSession(List, ...) is deprecated in favour of
            // SessionConfiguration, but still works and reads plainly.
            d.createCaptureSession(Collections.singletonList(input), new CameraCaptureSession.StateCallback() {
                @Override
                public void onConfigured(CameraCaptureSession s) {
                    session = s;
                    try {
                        CaptureRequest.Builder b = d.createCaptureRequest(CameraDevice.TEMPLATE_RECORD);
                        b.addTarget(input);
                        if (fps != null) b.set(CaptureRequest.CONTROL_AE_TARGET_FPS_RANGE, fps);
                        controls(b, settings);
                        request = b;  // kept, so update() changes only the controls
                        s.setRepeatingRequest(b.build(), null, h);
                        Log.i(Codecs.TAG, "camera capturing, AE target fps " + fps);
                    } catch (CameraAccessException | IllegalStateException e) {
                        fail("capture request: " + e);
                    }
                }

                @Override
                public void onConfigureFailed(CameraCaptureSession s) {
                    fail("capture session configuration failed");
                }
            }, h);
        } catch (CameraAccessException e) {
            fail("createCaptureSession: " + e);
        }
    }

    private void fail(String why) {
        if (failure == null) {
            failure = why;
            Log.e(Codecs.TAG, "camera role FAILED: " + why);
        }
    }

    /** The drain thread: encoder output into the queue, with a line a second. */
    private void drain() {
        MediaCodec.BufferInfo info = new MediaCodec.BufferInfo();
        byte[] config = null;
        boolean first = true, dropping = false;
        int frames = 0, keyframes = 0, dropped = 0, maxDepth = 0;
        long bytes = 0, windowStart = System.nanoTime();
        while (!stopped) {
            int index;
            try {
                index = encoder.dequeueOutputBuffer(info, 10_000);
            } catch (IllegalStateException e) {
                if (!stopped) fail("encoder: " + e);
                return;
            }
            if (index == MediaCodec.INFO_OUTPUT_FORMAT_CHANGED) {
                Log.i(Codecs.TAG, "encoder output format " + encoder.getOutputFormat());
            } else if (index >= 0) {
                ByteBuffer out = encoder.getOutputBuffer(index);
                byte[] data = new byte[info.size];
                out.position(info.offset);
                out.get(data);
                encoder.releaseOutputBuffer(index, false);
                if (first && info.size > 0) {
                    first = false;
                    boolean annexB = info.size >= 4 && data[0] == 0 && data[1] == 0 && data[2] == 0 && data[3] == 1;
                    Log.i(Codecs.TAG, "encoder first output " + info.size + " bytes, flags " + info.flags + ", "
                            + (annexB ? "Annex-B" : "NOT Annex-B: " + Arrays.toString(Arrays.copyOf(data, 4))));
                }
                if ((info.flags & MediaCodec.BUFFER_FLAG_CODEC_CONFIG) != 0) {
                    // SPS and PPS travel in front of the next IDR, as one
                    // frame flagged KEYFRAME and CONFIG (DECISIONS 5.3).
                    config = data;
                    continue;
                }
                boolean key = (info.flags & MediaCodec.BUFFER_FLAG_KEY_FRAME) != 0;
                Frame fr = new Frame();
                fr.presentUs = info.presentationTimeUs;
                fr.encodedUs = System.nanoTime() / 1000;
                fr.flags = key ? Link.FLAG_KEYFRAME : 0;
                if (key && config != null) {
                    fr.data = new byte[config.length + data.length];
                    System.arraycopy(config, 0, fr.data, 0, config.length);
                    System.arraycopy(data, 0, fr.data, config.length, data.length);
                    fr.flags |= Link.FLAG_CONFIG;
                    config = null;
                } else {
                    fr.data = data;
                }
                frames++;
                keyframes += key ? 1 : 0;
                bytes += fr.data.length;
                if (dropping && !key) {
                    dropped++;
                } else if (queue.offer(fr)) {
                    dropping = false;
                } else {
                    // Full: everything up to the next keyframe goes, since a
                    // lost P-frame breaks every frame after it.
                    dropped++;
                    if (!dropping) {
                        dropping = true;
                        Bundle b = new Bundle();
                        b.putInt(MediaCodec.PARAMETER_KEY_REQUEST_SYNC_FRAME, 0);
                        encoder.setParameters(b);
                        Log.w(Codecs.TAG, "camera queue full at " + QUEUE + ": dropping to the next keyframe");
                    }
                }
                maxDepth = Math.max(maxDepth, queue.size());
            }
            long now = System.nanoTime();
            if (now - windowStart >= 1_000_000_000L) {
                Log.i(Codecs.TAG, String.format("camera last second: %d frames, %d keyframes, %.2f Mbps, queue max %d, dropped %d",
                        frames, keyframes, bytes * 8 / ((now - windowStart) / 1e3), maxDepth, dropped));
                windowStart = now;
                frames = keyframes = dropped = maxDepth = 0;
                bytes = 0;
            }
        }
    }

    void stop() {
        stopped = true;
        try {
            if (session != null) session.close();
            if (camera != null) camera.close();
        } catch (IllegalStateException ignored) {
        }
        try {
            drainThread.join(1000);
        } catch (InterruptedException e) {
            Thread.currentThread().interrupt();
        }
        if (encoder != null) {
            try {
                encoder.stop();
            } catch (IllegalStateException ignored) {
            }
            encoder.release();
        }
        if (input != null) input.release();
        cameraThread.quitSafely();
        Log.i(Codecs.TAG, "camera role stopped");
    }
}
