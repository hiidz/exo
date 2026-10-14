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
 * front or back, which lens on that side, zoom, focus, exposure and torch.
 * The hub never sees them; CameraRole applies them, the lens on opening the
 * camera and the rest live, through the capture request.
 *
 * The lenses are the cameras Android gives apps that can send the webcam's
 * size to an encoder, labelled by their horizontal field of view, so the
 * same names work on any phone.
 */
final class CameraSettings {
    static final class Lens {
        String id;
        boolean front;
        int fov;              // horizontal, degrees
        boolean flash;
        float maxZoom = 1;
        float minFocus;       // diopters at the nearest focus; 0 for fixed focus
        int aeMin, aeMax;     // exposure compensation steps
        float aeStep = 1;     // EV a step

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
        if (s.side().isEmpty()) s.front = !s.front;
        return s;
    }

    /** Null for a camera that cannot send the webcam's size to an encoder. */
    private static Lens lens(CameraCharacteristics c, String id) {
        StreamConfigurationMap map = c.get(CameraCharacteristics.SCALER_STREAM_CONFIGURATION_MAP);
        Integer facing = c.get(CameraCharacteristics.LENS_FACING);
        if (map == null || facing == null || facing == CameraCharacteristics.LENS_FACING_EXTERNAL) return null;
        if (!Arrays.asList(map.getOutputSizes(MediaCodec.class))
                .contains(new android.util.Size(CameraRole.WIDTH, CameraRole.HEIGHT))) return null;
        Lens l = new Lens();
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

    void chooseLens(String id) {
        if (front) frontLens = id;
        else backLens = id;
    }

    /**
     * The tools' extras, through the same fields as the controls: --es facing
     * front or back, --es lens ID, --ef zoom, --ef focus (0 auto, else 0..1),
     * --ei exposure (steps), --ez torch.
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
    }

    @Override
    public String toString() {
        Lens l = lens();
        return l == null ? "no camera" : (front ? "front" : "back") + " lens " + l.id + " (" + l.fov + "°), zoom " + zoom
                + ", focus " + (focus == 0 ? "auto" : focus) + ", exposure " + exposure + ", torch " + torch;
    }

    private static SharedPreferences prefs(Context context) {
        return context.getSharedPreferences("exo", Context.MODE_PRIVATE);
    }
}
