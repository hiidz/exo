package exo.phone;

import android.app.Activity;
import android.content.Intent;
import android.graphics.Bitmap;
import android.graphics.Color;
import android.hardware.display.DisplayManager;
import android.os.Build;
import android.os.Bundle;
import android.util.Log;
import android.view.Display;
import android.view.Gravity;
import android.view.MotionEvent;
import android.view.Surface;
import android.view.SurfaceHolder;
import android.view.SurfaceView;
import android.view.View;
import android.view.WindowInsets;
import android.view.WindowInsetsController;
import android.widget.FrameLayout;
import android.widget.ImageView;
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
 * over localabstract:exo (DECISIONS 5.7) until the user leaves. MainActivity
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

    // The hub's pointer, drawn over the video (DECISIONS 11.1). The link
    // thread writes the latest state under cursorLock and posts one apply to
    // the UI thread; cursorPosted keeps it to one waiting at a time, so a
    // burst of positions comes to one move, with the newest.
    private ImageView cursorView;
    private final Object cursorLock = new Object();
    private Bitmap cursorShape;
    private int cursorHotX, cursorHotY, cursorX, cursorY;
    private boolean cursorVisible, cursorPosted;
    // UI thread only. The picture's size from the decoder; 0 until known.
    private int pictureW, pictureH;
    private boolean cursorShown;
    private Bitmap cursorApplied;

    // Measurement, one row per position, written per connection as
    // <run>-link-N-cursor.csv: hub_us on the PONG t2 clock, then recv, apply
    // and draw on this device's nanoTime. apply and draw stay 0 for a
    // position a newer one replaced before the UI thread took it, and draw
    // for one that moved nothing. Rows under cursorLock.
    private static final int MAX_CURSOR_ROWS = 200_000;
    private final ArrayList<long[]> cursorRows = new ArrayList<>();
    private long[] cursorLatest;  // the newest row, until applied
    private final ArrayList<long[]> awaitingDraw = new ArrayList<>();

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        root = new FrameLayout(this);
        view = new SurfaceView(this);
        view.setKeepScreenOn(true);
        root.addView(view, new FrameLayout.LayoutParams(
                FrameLayout.LayoutParams.MATCH_PARENT, FrameLayout.LayoutParams.MATCH_PARENT, Gravity.CENTER));
        setContentView(root);

        // Above the video, at the root's top-left, moved by translation.
        cursorView = new ImageView(this);
        cursorView.setScaleType(ImageView.ScaleType.FIT_XY);
        cursorView.setVisibility(View.GONE);
        root.addView(cursorView, new FrameLayout.LayoutParams(
                FrameLayout.LayoutParams.WRAP_CONTENT, FrameLayout.LayoutParams.WRAP_CONTENT,
                Gravity.TOP | Gravity.LEFT));
        // fit() changes the video's size and place; the mapping follows the
        // view as laid out, so it is logged and the cursor re-placed here.
        view.addOnLayoutChangeListener((v, l, t, r, b, ol, ot, or, ob) -> {
            if (pictureW == 0) return;
            Log.i(Codecs.TAG, String.format("map: picture %dx%d, view %dx%d at (%d,%d), sx %.4f sy %.4f",
                    pictureW, pictureH, v.getWidth(), v.getHeight(), l, t,
                    (double) v.getWidth() / pictureW, (double) v.getHeight() / pictureH));
            applyCursor();
        });
        // Just before each frame is drawn, on the UI thread: the moves
        // applied since the last one are in this frame.
        root.getViewTreeObserver().addOnDrawListener(() -> {
            if (awaitingDraw.isEmpty()) return;
            long now = System.nanoTime();
            synchronized (cursorLock) {
                for (long[] r : awaitingDraw) r[3] = now;
            }
            awaitingDraw.clear();
        });
        // Touch on the video goes up the display connection (FR-3.3).
        view.setOnTouchListener(this::onTouch);

        if (Build.VERSION.SDK_INT >= 30) {
            WindowInsetsController c = getWindow().getInsetsController();
            c.hide(WindowInsets.Type.systemBars());
            c.setSystemBarsBehavior(WindowInsetsController.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE);
        }

        // The panel's physical size, landscape: long side first.
        Display d = getSystemService(DisplayManager.class).getDisplay(Display.DEFAULT_DISPLAY);
        Display.Mode m = d.getMode();
        int pw = Math.max(m.getPhysicalWidth(), m.getPhysicalHeight());
        int ph = Math.min(m.getPhysicalWidth(), m.getPhysicalHeight());
        Log.i(Codecs.TAG, String.format("panel %dx%d at %.1fHz", pw, ph, d.getRefreshRate()));
        Codecs.logAvcDecoders(new int[][] {{2944, 1840}, {pw, ph}}, new int[] {30, 60});

        Intent in = getIntent();
        String name = in.getStringExtra("file");
        File dir = getExternalFilesDir(null);
        boolean paced = in.getBooleanExtra("paced", true);
        boolean lowLatency = in.getBooleanExtra("lowlatency", true);
        String codec = in.getStringExtra("codec");
        // Only a hint for choosing the decoder. Over the link the hub's
        // virtual display is meant to match the panel (NFR-3).
        int hintW = in.getIntExtra("w", name == null ? pw : 0), hintH = in.getIntExtra("h", name == null ? ph : 0);
        if (name == null) {
            statusView = new TextView(this);
            statusView.setTextSize(24);
            statusView.setTextColor(Color.WHITE);
            statusView.setGravity(Gravity.CENTER);
            statusView.setPadding(48, 48, 48, 48);
            root.addView(statusView, new FrameLayout.LayoutParams(
                    FrameLayout.LayoutParams.MATCH_PARENT, FrameLayout.LayoutParams.WRAP_CONTENT, Gravity.CENTER));
            showStatus(Link.WAITING);
        }
        // Link runs from this start are told apart by the time it happened.
        String linkRun = String.format("%tH%<tM%<tS", System.currentTimeMillis());

        // Both inputs need the Surface, so both start once it exists and stop
        // before it goes.
        view.getHolder().addCallback(new SurfaceHolder.Callback() {
            @Override
            public void surfaceCreated(SurfaceHolder holder) {
                Surface surface = holder.getSurface();
                if (name == null) {
                    link = new Link(Identity.of(DisplayActivity.this), in.getIntExtra("proto", Link.VERSION),
                            Link.ROLE_DISPLAY, new Link.Listener() {
                                @Override
                                public void onStatus(String sentence) {
                                    showStatus(sentence);
                                }

                                @Override
                                public Decoder newDecoder(String conn) {
                                    return Decoder.ofLink(file(conn + ".csv"), hintW, hintH,
                                            lowLatency, codec, surface, DisplayActivity.this::fit);
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
                    return;
                }
                String run = name + "-" + (paced ? "paced" : "fast") + (lowLatency ? "-ll" : "-noll")
                        + (codec != null ? "-" + codec : "");
                decoder = Decoder.ofFile(new File(dir, name), new File(dir, run + ".csv"),
                        in.getFloatExtra("fps", 30), hintW, hintH, paced, lowLatency, codec, surface,
                        DisplayActivity.this::fit);
                decoder.start();
            }

            @Override
            public void surfaceChanged(SurfaceHolder holder, int format, int width, int height) {
            }

            @Override
            public void surfaceDestroyed(SurfaceHolder holder) {
                if (link != null) link.stop();
                if (decoder != null) decoder.stop();
            }
        });
    }

    /** The link's sentence over the video, or nothing once connected. Any thread. */
    private void showStatus(String sentence) {
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
     * already relative to it; divide by the view's scale over the picture,
     * and clamp, so a drag that leaves the picture stays on its edge rather
     * than landing on another monitor. Historical samples are ignored: the
     * hub keeps only the latest position per finger anyway.
     */
    private boolean onTouch(View v, MotionEvent e) {
        int action = e.getActionMasked();
        if (pictureW == 0 || link == null) return true;
        double sx = (double) v.getWidth() / pictureW, sy = (double) v.getHeight() / pictureH;
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
            int px = Math.max(0, Math.min(pictureW - 1, (int) Math.round(e.getX(i) / sx)));
            int py = Math.max(0, Math.min(pictureH - 1, (int) Math.round(e.getY(i) / sy)));
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

    /** Any thread. Queues one apply unless one is already waiting. */
    private void postCursor() {
        synchronized (cursorLock) {
            if (cursorPosted) return;
            cursorPosted = true;
        }
        runOnUiThread(this::applyCursor);
    }

    /**
     * UI thread. Picture to view: the cursor's top-left is the hotspot less
     * the shape's hotspot, scaled by the video view's laid-out size over the
     * picture's, plus the video view's offset in the root.
     */
    private void applyCursor() {
        Bitmap shape;
        int hotX, hotY, x, y;
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
        }
        boolean show = visible && shape != null && pictureW != 0 && view.getWidth() != 0;
        if (!show) {
            cursorView.setVisibility(View.GONE);
            if (cursorShown) Log.i(Codecs.TAG, "cursor hidden");
            cursorShown = false;
            return;
        }
        double sx = (double) view.getWidth() / pictureW, sy = (double) view.getHeight() / pictureH;
        int w = (int) Math.round(shape.getWidth() * sx), h = (int) Math.round(shape.getHeight() * sy);
        int tx = (int) Math.round(view.getLeft() + (x - hotX) * sx), ty = (int) Math.round(view.getTop() + (y - hotY) * sy);
        // Placed by layout, not by translation. The window works out which
        // parts of it are see-through over the SurfaceView during layout
        // only, and SurfaceFlinger leaves those parts out. A view moved by
        // translation alone stays where the last layout put it as far as
        // that goes, and vanished once it moved (measured 2026-10-12).
        FrameLayout.LayoutParams lp = (FrameLayout.LayoutParams) cursorView.getLayoutParams();
        if (lp.width != w || lp.height != h || lp.leftMargin != tx || lp.topMargin != ty) {
            lp.width = w;
            lp.height = h;
            lp.leftMargin = tx;
            lp.topMargin = ty;
            cursorView.setLayoutParams(lp);
            if (row != null) awaitingDraw.add(row);
        }
        if (shape != cursorApplied) {
            cursorView.setImageBitmap(shape);
            cursorApplied = shape;
        }
        cursorView.setVisibility(View.VISIBLE);
        if (!cursorShown) {
            Log.i(Codecs.TAG, String.format("cursor shown: hotspot at picture (%d,%d), shape %dx%d hot (%d,%d),"
                    + " drawn at view (%d,%d) size %dx%d", x, y, shape.getWidth(), shape.getHeight(), hotX, hotY,
                    tx, ty, w, h));
        }
        cursorShown = true;
    }

    /** Sizes the view to the picture's aspect ratio, as large as fits. Any thread. */
    private void fit(int w, int h) {
        runOnUiThread(() -> {
            pictureW = w;
            pictureH = h;
            double scale = Math.min((double) root.getWidth() / w, (double) root.getHeight() / h);
            int vw = (int) Math.round(w * scale), vh = (int) Math.round(h * scale);
            Log.i(Codecs.TAG, String.format("picture %dx%d shown at %dx%d in %dx%d, scale %.3f",
                    w, h, vw, vh, root.getWidth(), root.getHeight(), scale));
            view.setLayoutParams(new FrameLayout.LayoutParams(vw, vh, Gravity.CENTER));
        });
    }
}
