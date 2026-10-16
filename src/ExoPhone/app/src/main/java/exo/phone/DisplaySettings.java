package exo.phone;

import android.content.Context;
import android.content.Intent;
import android.content.SharedPreferences;
import android.hardware.display.DisplayManager;
import android.util.DisplayMetrics;
import android.util.Log;
import android.view.Display;

import java.nio.ByteBuffer;
import java.util.ArrayList;
import java.util.List;

/**
 * The display role's settings, set on the phone and kept in the app's
 * preferences, and the one mode they make: what the display's HELLO carries
 * and the only mode the hub's monitor offers. Sizes are kept landscape, long
 * side first: always landscape, a portrait display being Windows' own
 * rotation of it.
 *
 * The size sent is the largest of the panel's own and same-aspect steps
 * below it that the decoder Decoder would pick takes at the chosen
 * rate; there is no picker, only the tools' extra: text size is Windows'
 * Scale.
 */
final class DisplaySettings {
    static final int[] RATES = {30, 60};
    // The smoothing buffer in ms, fixed: the one even at 60 on the TB375FC.
    static final int SMOOTHING = 50;
    // All of the panel, then three quarters, two thirds and half of it.
    private static final int[][] STEPS = {{1, 1}, {3, 4}, {2, 3}, {1, 2}};

    final int panelW, panelH;  // physical pixels, landscape
    final int mmW, mmH;        // the panel's size, landscape; 0 when not believable
    int fps = 60;
    int smoothing = SMOOTHING; // ms, 0 is off; only the tools change it, not kept
    String codec;              // a decoder name from the tools, as Decoder takes it; not kept
    final List<int[]> sizes = new ArrayList<>();  // landscape, those the decoder takes now
    int[] size;                // one of sizes, or null when it takes none

    private DisplaySettings(int panelW, int panelH, int mmW, int mmH) {
        this.panelW = panelW;
        this.panelH = panelH;
        this.mmW = mmW;
        this.mmH = mmH;
    }

    static DisplaySettings load(Context context) {
        Display d = context.getSystemService(DisplayManager.class).getDisplay(Display.DEFAULT_DISPLAY);
        Display.Mode m = d.getMode();
        int w = Math.max(m.getPhysicalWidth(), m.getPhysicalHeight());
        int h = Math.min(m.getPhysicalWidth(), m.getPhysicalHeight());
        // The density Android reports is sometimes a rounded bucket, not the
        // panel's, so a size that makes no sense goes as unknown.
        DisplayMetrics dm = new DisplayMetrics();
        d.getRealMetrics(dm);
        double dpi = (dm.xdpi + dm.ydpi) / 2;
        double inches = Math.hypot(w, h) / dpi;
        boolean believable = inches >= 3 && inches <= 20;
        DisplaySettings s = new DisplaySettings(w, h, believable ? (int) Math.round(w / dpi * 25.4) : 0,
                believable ? (int) Math.round(h / dpi * 25.4) : 0);
        Log.i(Codecs.TAG, String.format("panel %dx%d, xdpi %.1f ydpi %.1f, %.2f inch diagonal, %dx%dmm%s", w, h,
                dm.xdpi, dm.ydpi, inches, s.mmW, s.mmH, believable ? "" : " (not believable, sent as unknown)"));

        SharedPreferences p = prefs(context);
        s.fps = p.getInt("display.fps", 60);
        s.refresh(null);
        return s;
    }

    void save(Context context) {
        prefs(context).edit()
                .remove("display.portrait")  // now Windows' setting
                .putInt("display.fps", fps)
                .remove("display.size")  // no picker now: always the largest
                .apply();
    }

    /**
     * The tools' extras, through the same fields as the pickers: --ei rate,
     * --es size WxH or full, the largest the decoder takes, --ei smoothing
     * (ms, 0 off), and DisplayActivity's -e codec. Kept, as a picker's change would
     * be, but for size, smoothing and codec.
     */
    void apply(Intent in) {
        fps = in.getIntExtra("rate", fps);
        smoothing = in.getIntExtra("smoothing", smoothing);
        codec = in.getStringExtra("codec");
        String want = in.getStringExtra("size");
        refresh("full".equals(want) ? null : want != null ? want : size == null ? null : size[0] + "x" + size[1]);
    }

    /**
     * Works out the sizes for the rate now set, and keeps
     * the one named "WxH" if it is still among them, else the largest.
     */
    void refresh(String keep) {
        sizes.clear();
        for (int[] st : STEPS) {
            int w = even(panelW * st[0] / (double) st[1]), h = even(panelH * st[0] / (double) st[1]);
            if (!sizes.isEmpty() && sizes.get(sizes.size() - 1)[0] == w) continue;
            String decoder = Codecs.chooseDecoder(Codecs.decoderFormat(w, h, fps, true), codec);
            if (decoder != null) {
                sizes.add(new int[] {w, h});
            } else {
                Log.i(Codecs.TAG, "display size " + w + "x" + h + " at " + fps + "fps: no decoder takes it");
            }
        }
        size = sizes.isEmpty() ? null : sizes.get(0);
        for (int[] s : sizes) {
            if ((s[0] + "x" + s[1]).equals(keep)) size = s;
        }
    }

    int width() {
        return size[0];
    }

    int height() {
        return size[1];
    }

    /** The display role's fields after HELLO's rotation, laid out in the hub's Wire.cs. */
    byte[] helloTail() {
        return ByteBuffer.allocate(9).putShort((short) width()).putShort((short) height()).put((byte) fps)
                .putShort((short) mmW).putShort((short) mmH).array();
    }

    @Override
    public String toString() {
        return size == null ? "no size the decoder takes at " + fps + "fps"
                : width() + "x" + height() + " at " + fps + "fps"
                + ", smoothing " + (smoothing == 0 ? "off" : smoothing + "ms");
    }

    private static int even(double v) {
        return (int) Math.round(v / 2) * 2;
    }

    private static SharedPreferences prefs(Context context) {
        return context.getSharedPreferences("exo", Context.MODE_PRIVATE);
    }
}
