package exo.phone;

import android.app.Activity;
import android.content.Intent;
import android.content.pm.ActivityInfo;
import android.graphics.Bitmap;
import android.graphics.Canvas;
import android.graphics.Color;
import android.graphics.Paint;
import android.graphics.PixelFormat;
import android.graphics.PorterDuff;
import android.graphics.Rect;
import android.os.Build;
import android.os.Bundle;
import android.os.Handler;
import android.os.HandlerThread;
import android.util.Log;
import android.view.Choreographer;
import android.view.Display;
import android.view.Gravity;
import android.view.MotionEvent;
import android.view.SurfaceHolder;
import android.view.SurfaceView;
import android.view.View;
import android.view.WindowInsets;
import android.view.WindowInsetsController;
import android.view.WindowManager;
import android.widget.FrameLayout;
import android.widget.TextView;

import java.io.File;
import java.io.IOException;
import java.io.PrintWriter;
import java.nio.ByteBuffer;
import java.util.ArrayList;
import java.util.Arrays;

/**
 * The display role, started by MainActivity's "Use as display". Logs the
 * decoder capabilities, then either decodes the file named by the "file"
 * extra to a full-screen SurfaceView, or, without one, connects to the hub
 * through Link, over USB or Wi-Fi, until MainActivity's "Stop display";
 * out of view, the connection pauses and stays. MainActivity
 * passes its extras on, so the tools reach both through it:
 *
 *   adb shell am start -S -n exo.phone/.MainActivity --es role display [--ei proto 3]
 *
 * proto overrides the protocol version, only to prove a mismatch is
 * refused. With a file:
 *
 *   adb shell am start -S -n exo.phone/.MainActivity --es role display -e file vdd30.h264
 *       --ef fps 30 --ei w 2944 --ei h 1840 [--ez paced false] [--ez lowlatency false]
 *       [-e codec c2.mtk.avc.decoder.lowlatency]
 *
 * w and h are only a hint for choosing the decoder; the picture's geometry
 * comes from the decoder's output format. codec names the decoder; without
 * it findDecoderForFormat chooses.
 */
public class DisplayActivity extends Activity {
    private FrameLayout root;
    private SurfaceView view;
    private Decoder decoder;
    private Link link;
    // The link's sentence over the video: waiting, or why the PC refused.
    // Hidden while connected.
    private TextView statusView;
    // The mode this start sent, or sends once the window has turned.
    private DisplaySettings settings;

    // The hub's pointer, drawn over the video (DECISIONS 11.1) on a surface
    // of its own, as SuperDisplay does: a View over the SurfaceView shows
    // only inside the spot its last layout saw, so a fast pointer vanished
    // (PLAN.md, v3 step 2). The link thread writes the latest state under
    // cursorLock and posts one draw to the cursor thread; cursorPosted keeps
    // it to one waiting at a time, so a burst of positions comes to one
    // draw, with the newest. Drawing waits for a free buffer, which must
    // never hold up the link thread.
    private SurfaceView cursorSurface;
    private final HandlerThread cursorThread = new HandlerThread("cursor");
    private Handler cursorHandler;
    private final Object cursorLock = new Object();
    private Bitmap cursorShape;
    private int cursorHotX, cursorHotY, cursorX, cursorY;
    private boolean cursorVisible, cursorPosted;
    // The picture's size from the decoder, 0 until known; the cursor
    // surface's size as laid out, the video's. Under cursorLock.
    private int pictureW, pictureH, surfaceW, surfaceH;
    // Cursor thread only.
    private boolean cursorShown, surfaceClear = true;
    private final Paint cursorPaint = new Paint(Paint.FILTER_BITMAP_FLAG);
    private final Rect cursorDst = new Rect();

    // Measurement, one row per position, written per connection as
    // <run>-link-N-cursor.csv: hub_us on the PONG t2 clock, then recv, apply
    // and draw on this device's nanoTime. apply is when the cursor thread
    // took it, draw when its buffer was posted. Both stay 0 for a position a
    // newer one replaced first. Rows under cursorLock.
    private static final int MAX_CURSOR_ROWS = 200_000;
    private final ArrayList<long[]> cursorRows = new ArrayList<>();
    private long[] cursorLatest;  // the newest row, until applied

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        root = new FrameLayout(this);
        view = new SurfaceView(this);
        view.setKeepScreenOn(true);
        root.addView(view, new FrameLayout.LayoutParams(
                FrameLayout.LayoutParams.MATCH_PARENT, FrameLayout.LayoutParams.MATCH_PARENT, Gravity.CENTER));
        setContentView(root);

        // Above the video, the same size and place (fit() sets both), see-
        // through, so the picture's pixels map to it by scale alone. Not
        // clickable, so touch goes on to the video view beneath.
        cursorThread.start();
        cursorHandler = new Handler(cursorThread.getLooper());
        cursorSurface = new SurfaceView(this);
        cursorSurface.setZOrderMediaOverlay(true);
        cursorSurface.getHolder().setFormat(PixelFormat.TRANSLUCENT);
        root.addView(cursorSurface, new FrameLayout.LayoutParams(
                FrameLayout.LayoutParams.MATCH_PARENT, FrameLayout.LayoutParams.MATCH_PARENT, Gravity.CENTER));
        cursorSurface.getHolder().addCallback(new SurfaceHolder.Callback() {
            @Override
            public void surfaceCreated(SurfaceHolder holder) {
            }

            @Override
            public void surfaceChanged(SurfaceHolder holder, int format, int width, int height) {
                synchronized (cursorLock) {
                    surfaceW = width;
                    surfaceH = height;
                    Log.i(Codecs.TAG, String.format("cursor surface %dx%d, picture %dx%d", width, height,
                            pictureW, pictureH));
                }
                // A new buffer starts with nothing in it.
                cursorHandler.post(() -> surfaceClear = false);
                postCursor();
            }

            @Override
            public void surfaceDestroyed(SurfaceHolder holder) {
            }
        });
        // Touch on the video goes up the display connection (FR-3.3).
        view.setOnTouchListener(this::onTouch);

        if (Build.VERSION.SDK_INT >= 30) {
            WindowInsetsController c = getWindow().getInsetsController();
            c.hide(WindowInsets.Type.systemBars());
            c.setSystemBarsBehavior(WindowInsetsController.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE);
        }

        Intent in = getIntent();
        String name = in.getStringExtra("file");
        File dir = getExternalFilesDir(null);
        boolean paced = in.getBooleanExtra("paced", true);
        boolean lowLatency = in.getBooleanExtra("lowlatency", true);
        String codec = in.getStringExtra("codec");
        // The mode the hub's monitor gets: the settings MainActivity saved,
        // with the tools' extras it passed on.
        settings = DisplaySettings.load(this);
        settings.apply(in);
        Log.i(Codecs.TAG, "display settings: " + settings);
        Codecs.logAvcDecoders(new int[][] {{settings.panelW, settings.panelH}}, new int[] {30, 60});
        if (name == null) pinPanel(settings.fps);
        // Landscape, flipping with the device by the sensor whether or not
        // its rotation is locked, as SuperDisplay's default; portrait is
        // Windows' Display orientation (PLAN.md, Decide, Display).
        // configChanges keeps the activity, and the link, through any turn.
        // Where Android ignores this (Android 17 on a large screen), the
        // window turns as the device does and the picture is fitted to it.
        setRequestedOrientation(ActivityInfo.SCREEN_ORIENTATION_SENSOR_LANDSCAPE);
        // Refitted whenever the window's size changes, as on such a turn.
        root.addOnLayoutChangeListener((v, l, t, r, b, ol, ot, or, ob) -> {
            if (r - l != or - ol || b - t != ob - ot) {
                int w, h;
                synchronized (cursorLock) {
                    w = pictureW;
                    h = pictureH;
                }
                if (w != 0) fit(w, h);
            }
        });
        if (name == null) {
            running = this;
            if (Build.VERSION.SDK_INT >= 33) {
                getOnBackInvokedDispatcher().registerOnBackInvokedCallback(
                        android.window.OnBackInvokedDispatcher.PRIORITY_DEFAULT, this::back);
            }
            statusView = new TextView(this);
            statusView.setTextSize(24);
            statusView.setTextColor(Color.WHITE);
            statusView.setGravity(Gravity.CENTER);
            statusView.setPadding(48, 48, 48, 48);
            root.addView(statusView, new FrameLayout.LayoutParams(
                    FrameLayout.LayoutParams.MATCH_PARENT, FrameLayout.LayoutParams.WRAP_CONTENT, Gravity.CENTER));
            if (settings.size == null) {
                showStatus("This phone cannot decode the display at " + settings.fps + " fps. Pick another frame rate.");
                return;
            }
            showStatus(Link.WAITING);
        }
        // Only a hint for choosing the decoder from a file.
        int hintW = in.getIntExtra("w", 0);
        int hintH = in.getIntExtra("h", 0);
        // Link runs from this start are told apart by the time it happened.
        String linkRun = String.format("%tH%<tM%<tS", System.currentTimeMillis());

        // Both inputs need the Surface, so both start once it exists and stop
        // before it goes.
        view.getHolder().addCallback(new SurfaceHolder.Callback() {
            @Override
            public void surfaceChanged(SurfaceHolder holder, int format, int width, int height) {
                if (name != null) return;
                if (link != null) {
                    // Back in view. The same mode, held the same way,
                    // resumes the connection; otherwise it connects again,
                    // as a new start does (PLAN.md, Decide, Phone and protocol).
                    if (!link.paused()) return;
                    DisplayService.stop(DisplayActivity.this);
                    DisplaySettings now = DisplaySettings.load(DisplayActivity.this);
                    now.apply(getIntent());
                    if (now.size != null && settings.size != null
                            && Arrays.equals(now.helloTail(), settings.helloTail())) {
                        if (!link.resume()) {
                            Log.i(Codecs.TAG, "the hub now speaks below protocol 9; connecting again");
                            recreate();
                        }
                    } else {
                        Log.i(Codecs.TAG, "display settings changed while away: " + now + "; connecting again");
                        recreate();
                    }
                    return;
                }
                Log.i(Codecs.TAG, "display " + settings);
                link = new Link(DisplayActivity.this, Identity.of(DisplayActivity.this), in.getIntExtra("proto", Link.VERSION),
                        Link.ROLE_DISPLAY, new Link.Listener() {
                            @Override
                            public void onStatus(String sentence) {
                                showStatus(sentence);
                            }

                            @Override
                            public byte[] helloTail() {
                                return settings.helloTail();
                            }

                            @Override
                            public Decoder newDecoder(String conn, java.util.function.LongConsumer acks) {
                                return Decoder.ofLink(file(conn + ".csv"), settings.width(), settings.height(), settings.fps,
                                        lowLatency, codec, view.getHolder().getSurface(), DisplayActivity.this::fit, acks,
                                        settings.smoothing);
                            }

                            @Override
                            public CameraRole newCamera(String conn) {
                                throw new IllegalStateException("display role has no camera");
                            }

                            @Override
                            public File file(String name) {
                                return new File(dir, linkRun + "-" + name);
                            }

                            @Override
                            public void onCursorShape(Bitmap shape, int hotX, int hotY) {
                                synchronized (cursorLock) {
                                    cursorShape = shape;
                                    cursorHotX = hotX;
                                    cursorHotY = hotY;
                                }
                                postCursor();
                            }

                            @Override
                            public void onCursorPos(boolean visible, int x, int y, long hubUs) {
                                synchronized (cursorLock) {
                                    cursorVisible = visible;
                                    cursorX = x;
                                    cursorY = y;
                                    if (hubUs != 0) {
                                        long[] row = {hubUs, System.nanoTime(), 0, 0};
                                        if (cursorRows.size() < MAX_CURSOR_ROWS) cursorRows.add(row);
                                        cursorLatest = row;
                                    }
                                }
                                postCursor();
                            }

                            @Override
                            public void onLinkEnded(String conn) {
                                ArrayList<long[]> rows;
                                synchronized (cursorLock) {
                                    rows = new ArrayList<>(cursorRows);
                                    cursorRows.clear();
                                    cursorLatest = null;
                                }
                                writeCursorRows(file(conn + "-cursor.csv"), rows);
                            }
                        });
                link.start();
            }

            @Override
            public void surfaceCreated(SurfaceHolder holder) {
                if (name == null) return;
                String run = name + "-" + (paced ? "paced" : "fast") + (lowLatency ? "-ll" : "-noll")
                        + (codec != null ? "-" + codec : "");
                decoder = Decoder.ofFile(new File(dir, name), new File(dir, run + ".csv"),
                        in.getFloatExtra("fps", 30), hintW, hintH, paced, lowLatency, codec, holder.getSurface(),
                        DisplayActivity.this::fit);
                decoder.start();
            }

            @Override
            public void surfaceDestroyed(SurfaceHolder holder) {
                // Out of view: the connection pauses and stays, or, before
                // one was accepted or with an older hub, ends as before.
                if (link != null && !link.pause()) {
                    link.stop();
                    link = null;
                }
                if (link != null) DisplayService.start(DisplayActivity.this);
                if (decoder != null) decoder.stop();
            }
        });
    }

    /**
     * Holds the panel, while this window shows, at a refresh rate the stream
     * fits evenly: the lowest multiple of fps from 60 up, else the highest.
     * Left to itself, the S21 ran at 48Hz for 30fps, every frame 42ms.
     */
    private void pinPanel(int fps) {
        Display display = getWindowManager().getDefaultDisplay();
        Display.Mode now = display.getMode(), best = null;
        StringBuilder all = new StringBuilder();
        for (Display.Mode m : display.getSupportedModes()) {
            all.append(' ').append(m.getModeId()).append('=').append(m.getPhysicalWidth()).append('x')
                    .append(m.getPhysicalHeight()).append('@').append(m.getRefreshRate());
            if (m.getPhysicalWidth() != now.getPhysicalWidth() || m.getPhysicalHeight() != now.getPhysicalHeight()) continue;
            float k = m.getRefreshRate() / fps;
            if (Math.abs(k - Math.round(k)) > 0.02f) continue;
            float r = m.getRefreshRate(), b = best == null ? 0 : best.getRefreshRate();
            if (best == null || (r >= 59 ? b < 59 || r < b : b < 59 && r > b)) best = m;
        }
        Log.i(Codecs.TAG, "panel modes" + all + "; now " + now.getModeId() + ", pinned "
                + (best == null ? "none" : best.getModeId() + " at " + best.getRefreshRate() + "Hz") + " for " + fps + "fps");
        if (best == null) return;
        WindowManager.LayoutParams lp = getWindow().getAttributes();
        lp.preferredDisplayModeId = best.getModeId();
        getWindow().setAttributes(lp);
    }

    /** The link's sentence over the video, or nothing once connected. Any thread. */
    private void showStatus(String sentence) {
        status = sentence;
        runOnUiThread(() -> {
            statusView.setText(sentence);
            statusView.setVisibility(sentence == null ? View.GONE : View.VISIBLE);
        });
    }

    // TOUCH phases, as on the wire.
    private static final int DOWN = 1, MOVE = 2, UP = 3, CANCEL = 4;

    // One gesture, first finger down to last finger up, for its log line.
    // The gaps between events are the panel's touch report rate (R11).
    private long gestureStartNs, lastTouchNs;
    private final ArrayList<Long> touchGapsNs = new ArrayList<>();
    private int gestureEvents, gestureMaxPointers, gestureUnsent;

    /**
     * UI thread. One TOUCH per MotionEvent, carrying every finger. View to
     * picture: the listener is on the video view, so the event's x and y are
     * already relative to it; divided by the view's scale over the picture,
     * and clamped, so a drag that
     * leaves the picture stays on its edge rather than landing on another
     * monitor. Historical samples are ignored: the hub keeps only the latest
     * position per finger anyway.
     */
    private boolean onTouch(View v, MotionEvent e) {
        int action = e.getActionMasked();
        if (pictureW == 0 || link == null) return true;
        double vw = v.getWidth(), vh = v.getHeight();
        int n = e.getPointerCount();
        ByteBuffer b = ByteBuffer.allocate(8 + 2 + 2 + 1 + n * 10); // big-endian, as everything on the wire
        long now = System.nanoTime();
        b.putLong(now / 1000).putShort((short) pictureW).putShort((short) pictureH).put((byte) n);
        for (int i = 0; i < n; i++) {
            int phase = MOVE;
            if (action == MotionEvent.ACTION_CANCEL) {
                phase = CANCEL;
            } else if (i == e.getActionIndex()) {
                if (action == MotionEvent.ACTION_DOWN || action == MotionEvent.ACTION_POINTER_DOWN) phase = DOWN;
                if (action == MotionEvent.ACTION_UP || action == MotionEvent.ACTION_POINTER_UP) phase = UP;
            }
            int px = Math.max(0, Math.min(pictureW - 1, (int) Math.round(e.getX(i) / vw * pictureW)));
            int py = Math.max(0, Math.min(pictureH - 1, (int) Math.round(e.getY(i) / vh * pictureH)));
            b.put((byte) e.getPointerId(i)).put((byte) phase).putInt(px).putInt(py);
            if (phase == DOWN) {
                Log.i(Codecs.TAG, String.format("touch DOWN id %d at view (%.1f,%.1f) -> picture (%d,%d)",
                        e.getPointerId(i), e.getX(i), e.getY(i), px, py));
            }
        }
        boolean sent = link.sendTouch(b.array());

        if (action == MotionEvent.ACTION_DOWN) {
            gestureStartNs = now;
            touchGapsNs.clear();
            gestureEvents = gestureMaxPointers = gestureUnsent = 0;
        } else {
            touchGapsNs.add(now - lastTouchNs);
        }
        lastTouchNs = now;
        gestureEvents++;
        gestureMaxPointers = Math.max(gestureMaxPointers, n);
        if (!sent) gestureUnsent++;
        if (action == MotionEvent.ACTION_UP || action == MotionEvent.ACTION_CANCEL) {
            long[] g = new long[touchGapsNs.size()];
            for (int i = 0; i < g.length; i++) g[i] = touchGapsNs.get(i);
            Arrays.sort(g);
            Log.i(Codecs.TAG, String.format("touch gesture %s: %d events in %.0fms, up to %d fingers, not sent %d,"
                            + " gaps ms min %.1f median %.1f max %.1f", action == MotionEvent.ACTION_UP ? "ended" : "CANCELLED",
                    gestureEvents, (now - gestureStartNs) / 1e6, gestureMaxPointers, gestureUnsent,
                    g.length > 0 ? g[0] / 1e6 : 0, g.length > 0 ? g[g.length / 2] / 1e6 : 0,
                    g.length > 0 ? g[g.length - 1] / 1e6 : 0));
        }
        return true;
    }

    private static void writeCursorRows(File f, ArrayList<long[]> rows) {
        try (PrintWriter w = new PrintWriter(f)) {
            w.println("hub_us,recv_ns,apply_ns,draw_ns");
            for (long[] r : rows) w.printf("%d,%d,%d,%d%n", r[0], r[1], r[2], r[3]);
        } catch (IOException e) {
            Log.e(Codecs.TAG, "cannot write " + f, e);
        }
        Log.i(Codecs.TAG, "cursor rows " + rows.size() + " written to " + f.getName());
    }

    /**
     * Any thread. Queues one draw at the next vsync unless one is already
     * waiting. Once a vsync, not once a position: drawn as fast as buffers
     * freed, the surface's queue filled and each cursor reached the panel
     * about six vsyncs, 101.6ms median, after it was drawn
     * (t-curlat-surface).
     */
    private void postCursor() {
        synchronized (cursorLock) {
            if (cursorPosted) return;
            cursorPosted = true;
        }
        cursorHandler.post(() -> Choreographer.getInstance().postFrameCallback(t -> drawCursor()));
    }

    /**
     * Cursor thread. Clears the cursor surface and draws the shape with its
     * hotspot at the position, picture to surface by the surface's size over
     * the picture's. A hidden pointer clears it once.
     */
    private void drawCursor() {
        Bitmap shape;
        int hotX, hotY, x, y, pw, ph, w, h;
        boolean visible;
        long[] row;
        synchronized (cursorLock) {
            row = cursorLatest;
            cursorLatest = null;
            if (row != null) row[2] = System.nanoTime();
            cursorPosted = false;
            shape = cursorShape;
            hotX = cursorHotX;
            hotY = cursorHotY;
            x = cursorX;
            y = cursorY;
            visible = cursorVisible;
            pw = pictureW;
            ph = pictureH;
            w = surfaceW;
            h = surfaceH;
        }
        boolean show = visible && shape != null && pw != 0 && w != 0;
        if (!show && surfaceClear) return;
        SurfaceHolder holder = cursorSurface.getHolder();
        Canvas c;
        try {
            c = holder.lockHardwareCanvas();
        } catch (IllegalStateException | IllegalArgumentException e) {
            return;  // the surface went; surfaceChanged posts again
        }
        if (c == null) return;
        c.drawColor(Color.TRANSPARENT, PorterDuff.Mode.CLEAR);
        if (show) {
            // In picture pixels.
            c.save();
            c.scale((float) w / pw, (float) h / ph);
            cursorDst.set(x - hotX, y - hotY, x - hotX + shape.getWidth(), y - hotY + shape.getHeight());
            c.drawBitmap(shape, null, cursorDst, cursorPaint);
            c.restore();
        }
        holder.unlockCanvasAndPost(c);
        surfaceClear = !show;
        if (row != null) {
            long now = System.nanoTime();
            synchronized (cursorLock) {
                row[3] = now;
            }
        }
        if (show != cursorShown) {
            Log.i(Codecs.TAG, show ? String.format("cursor shown: hotspot at picture (%d,%d), shape %dx%d hot (%d,%d),"
                    + " picture rect %s on %dx%d", x, y, shape.getWidth(), shape.getHeight(), hotX, hotY,
                    cursorDst.toShortString(), w, h) : "cursor hidden");
            cursorShown = show;
        }
    }

    /**
     * Sizes the video, and the cursor surface over it, to the picture's
     * aspect ratio, as large as fits. Any thread.
     */
    private void fit(int w, int h) {
        runOnUiThread(() -> {
            synchronized (cursorLock) {
                pictureW = w;
                pictureH = h;
            }
            double scale = Math.min((double) root.getWidth() / w, (double) root.getHeight() / h);
            int vw = (int) Math.round(w * scale), vh = (int) Math.round(h * scale);
            Log.i(Codecs.TAG, String.format("picture %dx%d shown at %dx%d in %dx%d, scale %.3f",
                    w, h, vw, vh, root.getWidth(), root.getHeight(), scale));
            view.setLayoutParams(new FrameLayout.LayoutParams(vw, vh, Gravity.CENTER));
            cursorSurface.setLayoutParams(new FrameLayout.LayoutParams(vw, vh, Gravity.CENTER));
            postCursor();
        });
    }

    /** MainActivity's "Back to display" brings this one to the front. */
    @Override
    protected void onNewIntent(Intent intent) {
        super.onNewIntent(intent);
        setIntent(intent);
    }

    /**
     * Back goes to the app's screen and keeps the connection, paused, once
     * the hub has accepted it at protocol 9 or later; there "Stop display"
     * ends it. Before that, back ends the display as before.
     */
    private void back() {
        if (link != null && link.canPause()) {
            startActivity(new Intent(this, MainActivity.class).addFlags(Intent.FLAG_ACTIVITY_REORDER_TO_FRONT));
        } else {
            finish();
        }
    }

    @Override
    @SuppressWarnings("deprecation")
    public void onBackPressed() {
        back();  // below Android 13; from 13 the callback in onCreate
    }

    /** The display role's activity while it lives, connected or paused; else null. */
    static volatile DisplayActivity running;
    /** Its sentence while it runs, for the app's screen: waiting, or why the PC refused; null once connected. */
    static volatile String status;

    /** "Stop display": ends the display and its connection. Main thread. */
    static void end() {
        DisplayActivity a = running;
        if (a != null) a.finish();
    }

    @Override
    protected void onDestroy() {
        super.onDestroy();
        if (running == this) {
            running = null;
            status = null;
        }
        DisplayService.stop(this);
        if (link != null) link.stop();
        link = null;
        cursorThread.quitSafely();
    }
}
