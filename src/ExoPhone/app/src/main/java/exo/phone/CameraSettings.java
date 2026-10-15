package exo.phone;

import android.content.Context;
import android.content.Intent;
import android.content.SharedPreferences;
import android.hardware.camera2.CameraAccessException;
import android.hardware.camera2.CameraCharacteristics;
import android.hardware.camera2.CameraManager;
import android.hardware.camera2.params.StreamConfigurationMap;
import android.media.MediaCodec;
import android.os.Build;
import android.util.Log;
import android.util.Range;
import android.util.SizeF;

import java.util.ArrayList;
import java.util.Arrays;
import java.util.List;

/**
 * The webcam's settings, set on the phone and kept in the app's preferences:
 * front or back, which lens on that side, size, frame rate, zoom, focus,
 * exposure and torch. The hub sees only the size and rate, in HELLO, and
 * offers apps exactly that; CameraRole applies the rest, the lens on opening
 * the camera and the controls live, through the capture request.
 *
 * The lenses are the cameras Android gives apps that can send one of SIZES
 * to an encoder, labelled by their horizontal field of view, so the same
 * names work on any phone.
 */
final class CameraSettings {
    /**
     * The sizes on offer, largest first, all 16:9 as the sensor gives them.
     * 1920x1080 is the most one hub-to-camera frame carries.
     */
    static final int[][] SIZES = {{1920, 1080}, {1280, 720}, {640, 360}};
    static final int[] RATES = {30, 60};
    // The turn: Auto follows the phone between its two landscape ways; Fixed
    // holds the way it lay as the webcam started (CameraService.locked;
    // PLAN.md, Decide, Phone and protocol).
    static final int TURN_AUTO = 0, TURN_FIXED = 1;
    static final String[] TURNS = {"auto", "fixed"};

    static final class Lens {
        String id;
        boolean front;
        int fov;              // horizontal, degrees
        boolean flash;
        float maxZoom = 1;
        float minFocus;       // diopters at the nearest focus; 0 for fixed focus
        int aeMin, aeMax;     // exposure compensation steps
        float aeStep = 1;     // EV a step
        final List<int[]> sizes = new ArrayList<>();    // of SIZES, it can send to an encoder
        final List<int[]> sizes60 = new ArrayList<>();  // of those, the ones it can at 60fps

        String label(List<Lens> side) {
            // Against the side's first lens, which Android lists as its main.
            int main = side.get(0).fov;
            String kind = fov > main * 1.3 ? "Ultra wide" : fov < main / 1.4 ? "Telephoto" : "Wide";
            return kind + ", " + fov + "°";
        }
    }

    final List<Lens> lenses = new ArrayList<>();
    boolean front;
    private String backLens, frontLens;  // the chosen id per side
    float zoom = 1;
    float focus;        // 0: auto; otherwise 0..1 of the nearest focus distance
    int exposure;       // compensation steps
    boolean torch;
    int turn;           // TURN_AUTO or TURN_FIXED
    int width = 1280, height = 720, fps = 30;  // as chosen; size() and fps() are what this lens gets

    static CameraSettings load(Context context) {
        CameraSettings s = new CameraSettings();
        CameraManager cm = context.getSystemService(CameraManager.class);
        try {
            for (String id : cm.getCameraIdList()) {
                Lens l = lens(cm.getCameraCharacteristics(id), id);
                if (l != null) s.lenses.add(l);
            }
        } catch (CameraAccessException e) {
            Log.e(Codecs.TAG, "camera list: " + e);
        }
        SharedPreferences p = prefs(context);
        s.front = p.getBoolean("camera.front", false);
        s.backLens = p.getString("camera.lens.back", null);
        s.frontLens = p.getString("camera.lens.front", null);
        s.zoom = p.getFloat("camera.zoom", 1);
        s.focus = p.getFloat("camera.focus", 0);
        s.exposure = p.getInt("camera.exposure", 0);
        s.torch = p.getBoolean("camera.torch", false);
        s.turn = p.getInt("camera.turn", TURN_AUTO) == TURN_FIXED ? TURN_FIXED : TURN_AUTO;
        s.width = p.getInt("camera.width", 1280);
        s.height = p.getInt("camera.height", 720);
        s.fps = p.getInt("camera.fps", 30);
        if (s.side().isEmpty()) s.front = !s.front;
        return s;
    }

    /** Null for a camera that can send none of SIZES to an encoder. */
    private static Lens lens(CameraCharacteristics c, String id) {
        StreamConfigurationMap map = c.get(CameraCharacteristics.SCALER_STREAM_CONFIGURATION_MAP);
        Integer facing = c.get(CameraCharacteristics.LENS_FACING);
        if (map == null || facing == null || facing == CameraCharacteristics.LENS_FACING_EXTERNAL) return null;
        Lens l = new Lens();
        // 60fps needs both an exposure range reaching 60 and a frame time
        // short enough at that size.
        boolean ae60 = false;
        Range<Integer>[] ranges = c.get(CameraCharacteristics.CONTROL_AE_AVAILABLE_TARGET_FPS_RANGES);
        if (ranges != null) for (Range<Integer> r : ranges) ae60 |= r.getUpper() == 60;
        List<android.util.Size> out = Arrays.asList(map.getOutputSizes(MediaCodec.class));
        for (int[] s : SIZES) {
            android.util.Size size = new android.util.Size(s[0], s[1]);
            if (!out.contains(size)) continue;
            l.sizes.add(s);
            if (ae60 && map.getOutputMinFrameDuration(MediaCodec.class, size) <= 1_000_000_000L / 60) l.sizes60.add(s);
        }
        if (l.sizes.isEmpty()) return null;
        l.id = id;
        l.front = facing == CameraCharacteristics.LENS_FACING_FRONT;
        float[] focal = c.get(CameraCharacteristics.LENS_INFO_AVAILABLE_FOCAL_LENGTHS);
        SizeF sensor = c.get(CameraCharacteristics.SENSOR_INFO_PHYSICAL_SIZE);
        if (focal != null && focal.length > 0 && sensor != null) {
            l.fov = (int) Math.round(Math.toDegrees(2 * Math.atan(sensor.getWidth() / (2 * focal[0]))));
        }
        Boolean flash = c.get(CameraCharacteristics.FLASH_INFO_AVAILABLE);
        l.flash = flash != null && flash;
        if (Build.VERSION.SDK_INT >= 30) {
            Range<Float> z = c.get(CameraCharacteristics.CONTROL_ZOOM_RATIO_RANGE);
            if (z != null) l.maxZoom = z.getUpper();
        } else {
            Float z = c.get(CameraCharacteristics.SCALER_AVAILABLE_MAX_DIGITAL_ZOOM);
            if (z != null) l.maxZoom = z;
        }
        Float near = c.get(CameraCharacteristics.LENS_INFO_MINIMUM_FOCUS_DISTANCE);
        l.minFocus = near == null ? 0 : near;
        Range<Integer> ae = c.get(CameraCharacteristics.CONTROL_AE_COMPENSATION_RANGE);
        if (ae != null) {
            l.aeMin = ae.getLower();
            l.aeMax = ae.getUpper();
        }
        android.util.Rational step = c.get(CameraCharacteristics.CONTROL_AE_COMPENSATION_STEP);
        if (step != null) l.aeStep = step.floatValue();
        return l;
    }

    void save(Context context) {
        prefs(context).edit()
                .putBoolean("camera.front", front)
                .putString("camera.lens.back", backLens)
                .putString("camera.lens.front", frontLens)
                .putFloat("camera.zoom", zoom)
                .putFloat("camera.focus", focus)
                .putInt("camera.exposure", exposure)
                .putBoolean("camera.torch", torch)
                .putInt("camera.turn", turn)
                .putInt("camera.width", width)
                .putInt("camera.height", height)
                .putInt("camera.fps", fps)
                .apply();
    }

    /** The lenses on the chosen side, Android's order, its main first. */
    List<Lens> side() {
        List<Lens> l = new ArrayList<>();
        for (Lens x : lenses) if (x.front == front) l.add(x);
        return l;
    }

    /** The chosen lens on the chosen side, or its first; null with none. */
    Lens lens() {
        List<Lens> side = side();
        String want = front ? frontLens : backLens;
        for (Lens x : side) if (x.id.equals(want)) return x;
        return side.isEmpty() ? null : side.get(0);
    }

    /**
     * The size this lens sends: the one chosen if it can, else its largest.
     * Null with no lens.
     */
    int[] size() {
        Lens l = lens();
        if (l == null) return null;
        for (int[] s : l.sizes) if (s[0] == width && s[1] == height) return s;
        return l.sizes.get(0);
    }

    /** The rate this lens sends at size(): 60 if chosen and it can, else 30. */
    int fps() {
        Lens l = lens();
        return fps == 60 && l != null && l.sizes60.contains(size()) ? 60 : 30;
    }

    /** HELLO's camera fields: width u16, height u16, fps u8 (Wire.cs). */
    byte[] helloTail() {
        int[] s = size();
        if (s == null) s = SIZES[1];
        int r = fps();
        return new byte[]{(byte) (s[0] >> 8), (byte) s[0], (byte) (s[1] >> 8), (byte) s[1], (byte) r};
    }

    void chooseLens(String id) {
        if (front) frontLens = id;
        else backLens = id;
    }

    /**
     * The tools' extras, through the same fields as the controls: --es facing
     * front or back, --es lens ID, --ef zoom, --ef focus (0 auto, else 0..1),
     * --ei exposure (steps), --ez torch, --es turn (one of TURNS), --es
     * camsize WxH (one of SIZES), --ei camfps (30 or 60). Not size and rate,
     * which are the display's.
     */
    void apply(Intent in) {
        String f = in.getStringExtra("facing");
        if (f != null) front = f.equals("front");
        String id = in.getStringExtra("lens");
        if (id != null) chooseLens(id);
        zoom = in.getFloatExtra("zoom", zoom);
        focus = in.getFloatExtra("focus", focus);
        exposure = in.getIntExtra("exposure", exposure);
        torch = in.getBooleanExtra("torch", torch);
        int t = Arrays.asList(TURNS).indexOf(in.getStringExtra("turn"));
        if (t >= 0) turn = t;
        String size = in.getStringExtra("camsize");
        if (size != null && size.matches("\\d+x\\d+")) {
            width = Integer.parseInt(size.split("x")[0]);
            height = Integer.parseInt(size.split("x")[1]);
        }
        fps = in.getIntExtra("camfps", fps);
    }

    @Override
    public String toString() {
        Lens l = lens();
        return l == null ? "no camera" : (front ? "front" : "back") + " lens " + l.id + " (" + l.fov + "°), "
                + size()[0] + "x" + size()[1] + " at " + fps() + "fps, zoom " + zoom
                + ", focus " + (focus == 0 ? "auto" : focus) + ", exposure " + exposure + ", torch " + torch + ", turn " + TURNS[turn];
    }

    private static SharedPreferences prefs(Context context) {
        return context.getSharedPreferences("exo", Context.MODE_PRIVATE);
    }
}
