package exo.phone;

import android.app.Activity;
import android.content.Intent;
import android.graphics.Color;
import android.hardware.display.DisplayManager;
import android.os.Build;
import android.os.Bundle;
import android.util.Log;
import android.view.Display;
import android.view.Gravity;
import android.view.Surface;
import android.view.SurfaceHolder;
import android.view.SurfaceView;
import android.view.WindowInsets;
import android.view.WindowInsetsController;
import android.widget.FrameLayout;
import android.widget.TextView;

import java.io.File;

/**
 * The camera role, with --es role camera: the back camera to the hub over
 * localabstract:exo, for as long as this activity is in front with the
 * screen on (DECISIONS 10 item 4, increment D). The hub grants the camera
 * permission over adb before it launches this, so nothing asks on screen:
 *
 *   adb shell am start -S -n exo.phone/.MainActivity --es serial PHONESERIAL --es role camera
 *
 * Otherwise the display role. Logs the decoder capabilities, then either decodes the
 * file named by the "file" extra to a full-screen SurfaceView, or, without
 * one, connects to the hub over localabstract:exo (DECISIONS 5.7):
 *
 *   adb shell am start -S -n exo.phone/.MainActivity --es serial TABLETSERIAL [--ei proto 2]
 *
 * serial is the adb serial, which the hub supplies because the app cannot
 * read it. proto overrides the protocol version, only to prove a mismatch
 * is refused. With a file:
 *
 *   adb shell am start -S -n exo.phone/.MainActivity -e file vdd30.h264
 *       --ef fps 30 --ei w 2944 --ei h 1840 [--ez paced false] [--ez lowlatency false]
 *       [-e codec c2.mtk.avc.decoder.lowlatency]
 *
 * w and h are only a hint for choosing the decoder; the picture's geometry
 * comes from the decoder's output format. codec names the decoder; without
 * it findDecoderForFormat chooses.
 */
public class MainActivity extends Activity {
    private FrameLayout root;
    private SurfaceView view;
    private Decoder decoder;
    private Link link;

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        root = new FrameLayout(this);
        view = new SurfaceView(this);
        view.setKeepScreenOn(true);
        root.addView(view, new FrameLayout.LayoutParams(
                FrameLayout.LayoutParams.MATCH_PARENT, FrameLayout.LayoutParams.MATCH_PARENT, Gravity.CENTER));
        setContentView(root);

        Intent start = getIntent();
        if ("camera".equals(start.getStringExtra("role"))) {
            startCamera(start);
            return;
        }

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
        if (name == null) logSerialAccess();
        // Link runs from this start are told apart by the time it happened.
        String linkRun = String.format("%tH%<tM%<tS", System.currentTimeMillis());

        // Both inputs need the Surface, so both start once it exists and stop
        // before it goes.
        view.getHolder().addCallback(new SurfaceHolder.Callback() {
            @Override
            public void surfaceCreated(SurfaceHolder holder) {
                Surface surface = holder.getSurface();
                if (name == null) {
                    link = new Link(in.getStringExtra("serial"), in.getIntExtra("proto", Link.VERSION),
                            Link.ROLE_DISPLAY, new Link.Listener() {
                                @Override
                                public void onRefused(String reason) {
                                    showRefusal(reason);
                                }

                                @Override
                                public Decoder newDecoder(String conn) {
                                    return Decoder.ofLink(file(conn + ".csv"), hintW, hintH,
                                            lowLatency, codec, surface, MainActivity.this::fit);
                                }

                                @Override
                                public CameraRole newCamera(String conn) {
                                    throw new IllegalStateException("display role has no camera");
                                }

                                @Override
                                public File file(String name) {
                                    return new File(dir, linkRun + "-" + name);
                                }
                            });
                    link.start();
                    return;
                }
                String run = name + "-" + (paced ? "paced" : "fast") + (lowLatency ? "-ll" : "-noll")
                        + (codec != null ? "-" + codec : "");
                decoder = Decoder.ofFile(new File(dir, name), new File(dir, run + ".csv"),
                        in.getFloatExtra("fps", 30), hintW, hintH, paced, lowLatency, codec, surface,
                        MainActivity.this::fit);
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

    /** The camera role: the link, which makes a new camera for each connection. */
    private void startCamera(Intent in) {
        view.setVisibility(android.view.View.GONE);
        TextView t = new TextView(this);
        t.setText("Exo camera");
        t.setTextSize(28);
        root.addView(t, new FrameLayout.LayoutParams(
                FrameLayout.LayoutParams.WRAP_CONTENT, FrameLayout.LayoutParams.WRAP_CONTENT, Gravity.CENTER));
        // The view is gone, so the window holds the screen on instead.
        getWindow().addFlags(android.view.WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
        File dir = getExternalFilesDir(null);
        String run = String.format("%tH%<tM%<tS-camera", System.currentTimeMillis());
        link = new Link(in.getStringExtra("serial"), in.getIntExtra("proto", Link.VERSION), Link.ROLE_CAMERA,
                new Link.Listener() {
                    @Override
                    public void onRefused(String reason) {
                        showRefusal(reason);
                    }

                    @Override
                    public Decoder newDecoder(String conn) {
                        throw new IllegalStateException("camera role has no decoder");
                    }

                    @Override
                    public CameraRole newCamera(String conn) {
                        return new CameraRole(MainActivity.this);
                    }

                    @Override
                    public File file(String name) {
                        return new File(dir, run + "-" + name);
                    }
                });
        link.start();
    }

    @Override
    protected void onDestroy() {
        // The display role stops with its Surface; the camera role here.
        if (link != null && "camera".equals(getIntent().getStringExtra("role"))) link.stop();
        super.onDestroy();
    }

    /** A refusal is shown over the video, not only logged. Any thread. */
    private void showRefusal(String reason) {
        runOnUiThread(() -> {
            TextView t = new TextView(this);
            t.setText(reason);
            t.setTextSize(28);
            t.setTextColor(Color.RED);
            t.setBackgroundColor(Color.BLACK);
            t.setPadding(48, 48, 48, 48);
            root.addView(t, new FrameLayout.LayoutParams(
                    FrameLayout.LayoutParams.WRAP_CONTENT, FrameLayout.LayoutParams.WRAP_CONTENT, Gravity.CENTER));
        });
    }

    /**
     * Whether this app can read its own adb serial. DECISIONS 5.7 infers it
     * cannot on API 29 and later, which is why the hub passes it in.
     */
    private void logSerialAccess() {
        String api;
        try {
            api = Build.getSerial();
        } catch (SecurityException e) {
            api = "SecurityException: " + e.getMessage();
        }
        String prop;
        try {
            Process p = Runtime.getRuntime().exec(new String[] {"getprop", "ro.serialno"});
            prop = new java.io.BufferedReader(new java.io.InputStreamReader(p.getInputStream())).readLine();
            p.waitFor();
        } catch (Exception e) {
            prop = e.toString();
        }
        Log.i(Codecs.TAG, "serial: Build.getSerial() -> " + api + "; getprop ro.serialno -> \"" + prop + "\"");
    }

    /** Sizes the view to the picture's aspect ratio, as large as fits. Any thread. */
    private void fit(int w, int h) {
        runOnUiThread(() -> {
            double scale = Math.min((double) root.getWidth() / w, (double) root.getHeight() / h);
            int vw = (int) Math.round(w * scale), vh = (int) Math.round(h * scale);
            Log.i(Codecs.TAG, String.format("picture %dx%d shown at %dx%d in %dx%d, scale %.3f",
                    w, h, vw, vh, root.getWidth(), root.getHeight(), scale));
            view.setLayoutParams(new FrameLayout.LayoutParams(vw, vh, Gravity.CENTER));
        });
    }
}
