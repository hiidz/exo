package exo.phone;

import android.app.Activity;
import android.content.Intent;
import android.hardware.display.DisplayManager;
import android.os.Build;
import android.os.Bundle;
import android.util.Log;
import android.view.Display;
import android.view.Gravity;
import android.view.SurfaceHolder;
import android.view.SurfaceView;
import android.view.WindowInsets;
import android.view.WindowInsetsController;
import android.widget.FrameLayout;

import java.io.File;

/**
 * The display role, fed from a file. Logs the decoder capabilities, then
 * decodes the file named by the "file" extra to a full-screen SurfaceView.
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
    private FileDecoder decoder;

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        root = new FrameLayout(this);
        view = new SurfaceView(this);
        view.setKeepScreenOn(true);
        root.addView(view, new FrameLayout.LayoutParams(
                FrameLayout.LayoutParams.MATCH_PARENT, FrameLayout.LayoutParams.MATCH_PARENT, Gravity.CENTER));
        setContentView(root);

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
        if (name == null) return;
        File dir = getExternalFilesDir(null);
        boolean paced = in.getBooleanExtra("paced", true);
        boolean lowLatency = in.getBooleanExtra("lowlatency", true);
        String codec = in.getStringExtra("codec");
        String run = name + "-" + (paced ? "paced" : "fast") + (lowLatency ? "-ll" : "-noll")
                + (codec != null ? "-" + codec : "");

        view.getHolder().addCallback(new SurfaceHolder.Callback() {
            @Override
            public void surfaceCreated(SurfaceHolder holder) {
                decoder = new FileDecoder(new File(dir, name), new File(dir, run + ".csv"),
                        in.getFloatExtra("fps", 30), in.getIntExtra("w", 0), in.getIntExtra("h", 0),
                        paced, lowLatency, codec, holder.getSurface(), MainActivity.this::fit);
                decoder.start();
            }

            @Override
            public void surfaceChanged(SurfaceHolder holder, int format, int width, int height) {
            }

            @Override
            public void surfaceDestroyed(SurfaceHolder holder) {
                decoder.stop();
            }
        });
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
