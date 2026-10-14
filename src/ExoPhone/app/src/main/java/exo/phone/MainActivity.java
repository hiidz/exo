package exo.phone;

import android.Manifest;
import android.app.Activity;
import android.content.Intent;
import android.content.pm.PackageManager;
import android.os.Build;
import android.os.Bundle;
import android.util.Log;
import android.view.Gravity;
import android.view.View;
import android.view.WindowInsets;
import android.widget.AdapterView;
import android.widget.ArrayAdapter;
import android.widget.Button;
import android.widget.CheckBox;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.SeekBar;
import android.widget.Spinner;
import android.widget.TextView;

import java.util.ArrayList;
import java.util.Arrays;
import java.util.List;

/**
 * Exo's screen on the phone, where the role is picked (PLAN.md, Goal).
 * "Use as display" opens DisplayActivity, full screen, until the user
 * leaves it. "Use as webcam" starts CameraService, which goes on with the
 * screen off until the same button stops it, and asks for the camera
 * permission the first time. One phone may do both.
 *
 * The tools pick roles with an extra, through the same paths as the buttons:
 *
 *   adb shell am start -S -n exo.phone/.MainActivity --es role camera,display [--ei proto 3]
 *       [--ei rate 60] [--es orientation portrait] [--es size 1800x810]
 *
 * Started that way, it shows over the lock screen and wakes it, since the
 * webcam may start only while the app is visible. Every extra goes on to
 * DisplayActivity, and proto to CameraService too.
 */
public class MainActivity extends Activity {
    private static final int ASK_CAMERA = 1;

    private DisplaySettings settings;
    private Spinner sizePicker, orientationPicker, ratePicker;
    private CameraSettings camera;
    private Spinner facingPicker, lensPicker;
    private CheckBox torchBox;
    private SeekBar zoomBar, exposureBar, focusBar;
    private TextView focusLabel;
    private Button webcamButton;
    private TextView webcamStatus;
    // A sentence of this screen's own, such as a refused permission, shown
    // in place of the service's until the next start.
    private String webcamProblem;

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        LinearLayout root = new LinearLayout(this);
        root.setOrientation(LinearLayout.VERTICAL);
        root.setGravity(Gravity.CENTER);
        // From Android 15 the window reaches under the system bars, which in
        // landscape put the navigation bar over the left edge (seen on the
        // S21), so the bars' insets are added to the padding.
        root.setOnApplyWindowInsetsListener((v, insets) -> {
            int l, t, r, b;
            if (Build.VERSION.SDK_INT >= 30) {
                android.graphics.Insets i = insets.getInsets(
                        WindowInsets.Type.systemBars() | WindowInsets.Type.displayCutout());
                l = i.left;
                t = i.top;
                r = i.right;
                b = i.bottom;
            } else {
                l = insets.getSystemWindowInsetLeft();
                t = insets.getSystemWindowInsetTop();
                r = insets.getSystemWindowInsetRight();
                b = insets.getSystemWindowInsetBottom();
            }
            v.setPadding(64 + l, 64 + t, 64 + r, 64 + b);
            return insets;
        });

        TextView title = new TextView(this);
        title.setText("Exo");
        title.setTextSize(32);
        root.addView(title);
        TextView about = new TextView(this);
        about.setText("Use this phone as a display or a webcam for your PC.");
        about.setPadding(0, 16, 0, 32);
        root.addView(about);

        Button display = new Button(this);
        display.setText("Use as display");
        display.setOnClickListener(v -> useDisplay());
        root.addView(display);
        // The display's settings, under its button. The hub's monitor gets
        // exactly this mode the next time the display starts.
        settings = DisplaySettings.load(this);
        LinearLayout pickers = new LinearLayout(this);
        sizePicker = picker(pickers, this::picked);
        orientationPicker = picker(pickers, this::picked);
        ratePicker = picker(pickers, this::picked);
        root.addView(pickers);
        bindPickers();
        webcamButton = new Button(this);
        webcamButton.setOnClickListener(v -> {
            if (CameraService.running) {
                stopService(new Intent(this, CameraService.class));
            } else {
                useWebcam();
            }
        });
        root.addView(webcamButton);
        // The webcam's settings, under its button. Zoom, exposure, focus and
        // torch change the picture as they move; side and lens reopen the
        // camera, a second's gap.
        camera = CameraSettings.load(this);
        LinearLayout camPickers = new LinearLayout(this);
        facingPicker = picker(camPickers, this::cameraPicked);
        lensPicker = picker(camPickers, this::cameraPicked);
        torchBox = new CheckBox(this);
        torchBox.setText("Torch");
        torchBox.setOnCheckedChangeListener((box, on) -> {
            if (camera.torch == on) return;
            camera.torch = on;
            cameraChanged();
        });
        camPickers.addView(torchBox, new LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1));
        root.addView(camPickers);
        LinearLayout sliders = new LinearLayout(this);
        zoomBar = slider(sliders, "Zoom");
        exposureBar = slider(sliders, "Exposure");
        focusBar = slider(sliders, "Focus");
        focusLabel = (TextView) ((LinearLayout) focusBar.getParent()).getChildAt(0);
        root.addView(sliders);
        bindCamera();
        webcamStatus = new TextView(this);
        webcamStatus.setPadding(0, 16, 0, 0);
        root.addView(webcamStatus);
        // Scrolls when a short landscape phone cannot fit it all.
        ScrollView scroll = new ScrollView(this);
        scroll.setFillViewport(true);
        scroll.addView(root);
        setContentView(scroll);

        if (savedInstanceState == null) fromTools(getIntent());
    }

    @Override
    protected void onNewIntent(Intent intent) {
        super.onNewIntent(intent);
        setIntent(intent);
        fromTools(intent);
    }

    /** The role extra, if any: "display", "camera" or both, comma separated. */
    private void fromTools(Intent in) {
        String roles = in.getStringExtra("role");
        if (roles == null) return;
        // Arrays.asList, not List.of: minSdk is 29 and List.of came in 30.
        List<String> given = Arrays.asList(roles.split(","));
        Log.i(Codecs.TAG, "roles from the intent: " + given);
        setShowWhenLocked(true);
        setTurnScreenOn(true);
        if (given.contains("camera")) {
            camera.apply(in);
            bindCamera();
            cameraChanged();
            if (!CameraService.running) useWebcam();
        }
        if (given.contains("display")) {
            settings.apply(in);
            settings.save(this);
            bindPickers();
            useDisplay();
        }
    }

    private Spinner picker(LinearLayout row, Runnable onPick) {
        Spinner s = new Spinner(this);
        row.addView(s, new LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1));
        s.setOnItemSelectedListener(new AdapterView.OnItemSelectedListener() {
            @Override
            public void onItemSelected(AdapterView<?> parent, View view, int position, long id) {
                onPick.run();
            }

            @Override
            public void onNothingSelected(AdapterView<?> parent) {
            }
        });
        return s;
    }

    /**
     * A picker moved: the settings follow it and are kept, and the sizes are
     * worked out again for the rate and orientation. Spinners also report
     * the selections bindPickers makes, later, so a report that changes
     * nothing is ignored.
     */
    private void picked() {
        int at = sizePicker.getSelectedItemPosition();
        int[] size = at >= 0 && at < settings.sizes.size() ? settings.sizes.get(at) : settings.size;
        boolean portrait = orientationPicker.getSelectedItemPosition() == 1;
        int fps = DisplaySettings.RATES[Math.max(0, ratePicker.getSelectedItemPosition())];
        if (size == settings.size && portrait == settings.portrait && fps == settings.fps) return;
        settings.portrait = portrait;
        settings.fps = fps;
        settings.refresh(size == null ? null : size[0] + "x" + size[1]);
        settings.save(this);
        Log.i(Codecs.TAG, "display settings picked: " + settings);
        bindPickers();
    }

    /** A labelled slider, sharing a row with others. */
    private SeekBar slider(LinearLayout row, String label) {
        LinearLayout cell = new LinearLayout(this);
        cell.setGravity(Gravity.CENTER_VERTICAL);
        TextView t = new TextView(this);
        t.setText(label);
        cell.addView(t);
        SeekBar bar = new SeekBar(this);
        bar.setOnSeekBarChangeListener(new SeekBar.OnSeekBarChangeListener() {
            @Override
            public void onProgressChanged(SeekBar b, int progress, boolean fromUser) {
                if (fromUser) slid(b, progress);
            }

            @Override
            public void onStartTrackingTouch(SeekBar b) {
            }

            @Override
            public void onStopTrackingTouch(SeekBar b) {
            }
        });
        cell.addView(bar, new LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1));
        row.addView(cell, new LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1));
        return bar;
    }

    private void slid(SeekBar b, int progress) {
        CameraSettings.Lens lens = camera.lens();
        if (lens == null) return;
        if (b == zoomBar) camera.zoom = 1 + progress / 100f * (lens.maxZoom - 1);
        else if (b == exposureBar) camera.exposure = lens.aeMin + progress;
        else camera.focus = progress / 100f;
        focusLabel.setText(camera.focus == 0 ? "Focus: auto" : "Focus");
        cameraChanged();
    }

    // The sides with a lens, in the facing picker's order: false back, true front.
    private final List<Boolean> sides = new ArrayList<>();

    /** The webcam's controls, from camera, each shown only where this lens has it. */
    private void bindCamera() {
        sides.clear();
        List<String> names = new ArrayList<>();
        for (boolean front : new boolean[] {false, true}) {
            for (CameraSettings.Lens l : camera.lenses) {
                if (l.front != front) continue;
                sides.add(front);
                names.add(front ? "Front camera" : "Back camera");
                break;
            }
        }
        if (sides.isEmpty()) names.add("No camera");
        bind(facingPicker, names, Math.max(0, sides.indexOf(camera.front)));
        List<CameraSettings.Lens> side = camera.side();
        CameraSettings.Lens lens = camera.lens();
        List<String> lenses = new ArrayList<>();
        for (CameraSettings.Lens l : side) lenses.add(l.label(side));
        if (lenses.isEmpty()) lenses.add("No lens");
        bind(lensPicker, lenses, Math.max(0, side.indexOf(lens)));
        boolean any = lens != null;
        torchBox.setVisibility(any && lens.flash ? View.VISIBLE : View.INVISIBLE);
        torchBox.setChecked(camera.torch);
        zoomBar.setMax(100);
        zoomBar.setEnabled(any && lens.maxZoom > 1);
        zoomBar.setProgress(any && lens.maxZoom > 1 ? Math.round((camera.zoom - 1) / (lens.maxZoom - 1) * 100) : 0);
        exposureBar.setMax(any ? lens.aeMax - lens.aeMin : 0);
        exposureBar.setProgress(any ? camera.exposure - lens.aeMin : 0);
        ((View) focusBar.getParent()).setVisibility(any && lens.minFocus > 0 ? View.VISIBLE : View.INVISIBLE);
        focusBar.setMax(100);
        focusBar.setProgress(Math.round(camera.focus * 100));
        focusLabel.setText(camera.focus == 0 ? "Focus: auto" : "Focus");
    }

    /** The side or lens picker moved. Spinners report bindCamera's selections too, which change nothing. */
    private void cameraPicked() {
        if (sides.isEmpty()) return;
        boolean front = sides.get(Math.max(0, facingPicker.getSelectedItemPosition()));
        if (front != camera.front) {
            camera.front = front;  // that side's lens is the one last chosen there
        } else {
            List<CameraSettings.Lens> side = camera.side();
            int at = lensPicker.getSelectedItemPosition();
            if (at < 0 || at >= side.size() || side.get(at) == camera.lens()) return;
            camera.chooseLens(side.get(at).id);
        }
        cameraChanged();
        bindCamera();
    }

    /** Kept, and into the running webcam: live, or by reopening the camera for another lens. */
    private void cameraChanged() {
        camera.save(this);
        Log.i(Codecs.TAG, "webcam settings: " + camera);
        CameraService.settingsChanged(this);
    }

    private void bindPickers() {
        List<String> sizes = new ArrayList<>();
        for (int[] s : settings.sizes) sizes.add(settings.label(s));
        if (sizes.isEmpty()) sizes.add("No size at " + settings.fps + " fps");
        bind(sizePicker, sizes, Math.max(0, settings.sizes.indexOf(settings.size)));
        bind(orientationPicker, Arrays.asList("Landscape", "Portrait"), settings.portrait ? 1 : 0);
        bind(ratePicker, Arrays.asList("30 fps", "60 fps"), settings.fps == 60 ? 1 : 0);
    }

    private void bind(Spinner s, List<String> items, int selected) {
        ArrayAdapter<String> a = new ArrayAdapter<>(this, android.R.layout.simple_spinner_item, items);
        a.setDropDownViewResource(android.R.layout.simple_spinner_dropdown_item);
        s.setAdapter(a);
        s.setSelection(selected);
    }

    @Override
    protected void onResume() {
        super.onResume();
        CameraService.onChange = () -> runOnUiThread(this::refresh);
        refresh();
    }

    @Override
    protected void onPause() {
        CameraService.onChange = null;
        super.onPause();
    }

    private void refresh() {
        webcamButton.setText(CameraService.running ? "Stop webcam" : "Use as webcam");
        String s = webcamProblem != null ? webcamProblem : CameraService.status;
        webcamStatus.setText(s);
        webcamStatus.setVisibility(s == null ? View.GONE : View.VISIBLE);
    }

    private void useDisplay() {
        startActivity(new Intent(this, DisplayActivity.class).putExtras(getIntent()));
    }

    private void useWebcam() {
        if (checkSelfPermission(Manifest.permission.CAMERA) != PackageManager.PERMISSION_GRANTED) {
            // The notification shows the webcam's state; without it the
            // service runs all the same.
            String[] want = Build.VERSION.SDK_INT >= 33
                    ? new String[] {Manifest.permission.CAMERA, Manifest.permission.POST_NOTIFICATIONS}
                    : new String[] {Manifest.permission.CAMERA};
            requestPermissions(want, ASK_CAMERA);
            return;
        }
        webcamProblem = null;
        Intent svc = new Intent(this, CameraService.class)
                .putExtra("proto", getIntent().getIntExtra("proto", Link.VERSION));
        try {
            startForegroundService(svc);
            Log.i(Codecs.TAG, "camera service start requested");
        } catch (RuntimeException e) {
            Log.e(Codecs.TAG, "camera service start FAILED: " + e);
            webcamProblem = "Android did not let the webcam start: " + e.getMessage();
        }
        refresh();
    }

    @Override
    public void onRequestPermissionsResult(int request, String[] permissions, int[] results) {
        if (request != ASK_CAMERA) return;
        if (checkSelfPermission(Manifest.permission.CAMERA) == PackageManager.PERMISSION_GRANTED) {
            useWebcam();
            return;
        }
        webcamProblem = "Exo needs the camera permission to be a webcam. Allow it in Settings, Apps, Exo.";
        refresh();
    }
}
