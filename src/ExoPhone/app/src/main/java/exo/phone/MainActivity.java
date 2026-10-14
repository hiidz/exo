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
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.Spinner;
import android.widget.TextView;

import java.util.ArrayList;
import java.util.Arrays;
import java.util.List;

/**
 * Exo's screen on the phone, where the role is picked (CLAUDE.md, Decide).
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
        sizePicker = picker(pickers);
        orientationPicker = picker(pickers);
        ratePicker = picker(pickers);
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
        if (given.contains("camera") && !CameraService.running) useWebcam();
        if (given.contains("display")) {
            settings.apply(in);
            settings.save(this);
            bindPickers();
            useDisplay();
        }
    }

    private Spinner picker(LinearLayout row) {
        Spinner s = new Spinner(this);
        row.addView(s, new LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1));
        s.setOnItemSelectedListener(new AdapterView.OnItemSelectedListener() {
            @Override
            public void onItemSelected(AdapterView<?> parent, View view, int position, long id) {
                picked();
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
