package exo.phone;

import android.Manifest;
import android.app.Activity;
import android.app.AlertDialog;
import android.content.Intent;
import android.content.pm.PackageManager;
import android.net.Network;
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
import android.widget.EditText;
import android.widget.LinearLayout;
import android.widget.RadioButton;
import android.widget.RadioGroup;
import android.widget.ScrollView;
import android.widget.SeekBar;
import android.widget.Spinner;
import android.widget.TextView;

import java.util.ArrayList;
import java.util.Arrays;
import java.util.List;

/**
 * Exo's screen on the phone, where the role is picked (PLAN.md, v2, done).
 * "Use as display" opens DisplayActivity, full screen, until the user
 * leaves it. "Use as webcam" starts CameraService, which goes on with the
 * screen off until the same button stops it, and asks for the camera
 * permission the first time. One phone may do both.
 *
 * The tools pick roles with an extra, through the same paths as the buttons:
 *
 *   adb shell am start -S -n exo.phone/.MainActivity --es role camera,display [--ei proto 3]
 *       [--ei rate 60] [--es orientation portrait] [--es size 1800x810]
 *       [--es camsize 1920x1080] [--ei camfps 60] (the rest in CameraSettings.apply)
 *       [--es via usb|wifi] [--es pc NAME | --es ip ADDRESS] (kept, as the picker keeps them)
 *
 * Started that way, it shows over the lock screen and wakes it, since the
 * webcam may start only while the app is visible. Every extra goes on to
 * DisplayActivity, and proto to CameraService too.
 */
public class MainActivity extends Activity {
    private static final int ASK_CAMERA = 1, ASK_LOCAL_NETWORK = 2;
    private static final String LOCAL_NETWORK = Discovery.LOCAL_NETWORK;

    private Via via;
    private RadioButton usbButton, wifiButton;
    private Button pcButton;
    private TextView viaStatus;

    private DisplaySettings settings;
    private Spinner sizePicker, orientationPicker, ratePicker, smoothingPicker;
    private CameraSettings camera;
    private Spinner facingPicker, lensPicker, camSizePicker, camRatePicker;
    private final List<Integer> camRates = new ArrayList<>();  // the rate picker's, in its order
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

        // How to reach the PC, over the roles, since both use it. A change
        // reaches a role the next time it starts.
        via = Via.load(this);
        RadioGroup viaRow = new RadioGroup(this);
        viaRow.setOrientation(LinearLayout.HORIZONTAL);
        viaRow.setGravity(Gravity.CENTER_VERTICAL);
        TextView viaLabel = new TextView(this);
        viaLabel.setText("Connect by");
        viaRow.addView(viaLabel);
        usbButton = new RadioButton(this);
        usbButton.setText("USB");
        usbButton.setId(View.generateViewId());
        viaRow.addView(usbButton);
        wifiButton = new RadioButton(this);
        wifiButton.setText("Wi-Fi");
        wifiButton.setId(View.generateViewId());
        viaRow.addView(wifiButton);
        pcButton = new Button(this);
        pcButton.setOnClickListener(v -> findPcs());
        viaRow.addView(pcButton);
        viaRow.check(via.wifi ? wifiButton.getId() : usbButton.getId());
        viaRow.setOnCheckedChangeListener((g, id) -> {
            boolean wifi = id == wifiButton.getId();
            if (wifi == via.wifi) return;
            setVia(new Via(wifi, via.pc, via.ip));
            if (wifi && via.target() == null) findPcs();
        });
        root.addView(viaRow);
        viaStatus = new TextView(this);
        viaStatus.setPadding(0, 0, 0, 16);
        root.addView(viaStatus);
        bindVia();

        Button display = new Button(this);
        display.setText("Use as display");
        display.setOnClickListener(v -> useDisplay());
        root.addView(display);
        // The display's settings, under its button. The hub's monitor gets
        // exactly this mode, and the decoder this smoothing, the next time the
        // display starts.
        settings = DisplaySettings.load(this);
        LinearLayout pickers = new LinearLayout(this);
        sizePicker = picker(pickers, this::picked);
        orientationPicker = picker(pickers, this::picked);
        ratePicker = picker(pickers, this::picked);
        smoothingPicker = picker(pickers, this::picked);
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
        // torch change the picture as they move; side, lens, size and rate
        // reopen the camera, a second's gap, and the hub registers the
        // camera again for a new size or rate, so an app using it must reopen it.
        camera = CameraSettings.load(this);
        LinearLayout camPickers = new LinearLayout(this);
        facingPicker = picker(camPickers, this::cameraPicked);
        lensPicker = picker(camPickers, this::cameraPicked);
        camSizePicker = picker(camPickers, this::cameraPicked);
        camRatePicker = picker(camPickers, this::cameraPicked);
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
        String v = in.getStringExtra("via");
        if (v != null) {
            String ip = in.getStringExtra("ip"), pc = in.getStringExtra("pc");
            setVia(new Via(v.equals("wifi"), ip != null ? null : pc != null ? pc : via.pc, ip));
            Log.i(Codecs.TAG, "via from the intent: " + via);
        }
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
        int smoothing = DisplaySettings.SMOOTHING[Math.max(0, smoothingPicker.getSelectedItemPosition())];
        if (size == settings.size && portrait == settings.portrait && fps == settings.fps
                && smoothing == settings.smoothing) return;
        settings.smoothing = smoothing;
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
        int[] size = camera.size();
        List<String> sizes = new ArrayList<>();
        if (any) for (int[] s : lens.sizes) sizes.add(s[0] + "x" + s[1]);
        if (sizes.isEmpty()) sizes.add("No size");
        bind(camSizePicker, sizes, any ? Math.max(0, lens.sizes.indexOf(size)) : 0);
        camRates.clear();
        List<String> rates = new ArrayList<>();
        for (int r : CameraSettings.RATES) {
            if (r == 60 && !(any && lens.sizes60.contains(size))) continue;
            camRates.add(r);
            rates.add(r + " fps");
        }
        bind(camRatePicker, rates, Math.max(0, camRates.indexOf(camera.fps())));
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

    /**
     * The side, lens, size or rate picker moved. Spinners report
     * bindCamera's selections too, which change nothing.
     */
    private void cameraPicked() {
        if (sides.isEmpty()) return;
        boolean front = sides.get(Math.max(0, facingPicker.getSelectedItemPosition()));
        CameraSettings.Lens lens = camera.lens();
        List<CameraSettings.Lens> side = camera.side();
        int at = lensPicker.getSelectedItemPosition();
        int s = camSizePicker.getSelectedItemPosition();
        int r = camRatePicker.getSelectedItemPosition();
        if (front != camera.front) {
            camera.front = front;  // that side's lens is the one last chosen there
        } else if (at >= 0 && at < side.size() && side.get(at) != lens) {
            camera.chooseLens(side.get(at).id);
        } else if (lens != null && s >= 0 && s < lens.sizes.size() && lens.sizes.get(s) != camera.size()) {
            camera.width = lens.sizes.get(s)[0];
            camera.height = lens.sizes.get(s)[1];
        } else if (r >= 0 && r < camRates.size() && camRates.get(r) != camera.fps()) {
            camera.fps = camRates.get(r);
        } else {
            return;
        }
        cameraChanged();
        bindCamera();
    }

    /** Kept, and into the running webcam: live, or by reopening the camera for another lens, size or rate. */
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
        bind(smoothingPicker, Arrays.asList("Smoothing off", "Smoothing 20 ms", "Smoothing 50 ms"),
                Math.max(0, Arrays.binarySearch(DisplaySettings.SMOOTHING, settings.smoothing)));
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

    private void setVia(Via v) {
        via = v;
        via.save(this);
        Log.i(Codecs.TAG, "connect by " + via);
        bindVia();
    }

    private void bindVia() {
        pcButton.setVisibility(via.wifi ? View.VISIBLE : View.GONE);
        pcButton.setText(via.target() == null ? "Pick a PC" : "PC: " + via.target());
        if (!via.wifi) viaStatus.setText("Plug this phone into the PC, with USB debugging on.");
        else if (needsLocalNetwork()) viaStatus.setText(Link.NEEDS_PERMISSION);
        else viaStatus.setText("On the same Wi-Fi as the PC. The PC asks to allow this phone the first time.");
    }

    private boolean needsLocalNetwork() {
        return Discovery.needsPermission(this);
    }

    /** Asks the network for PCs, then lets the user pick one or type an address. */
    private void findPcs() {
        if (needsLocalNetwork()) {
            requestPermissions(new String[] {LOCAL_NETWORK}, ASK_LOCAL_NETWORK);
            return;
        }
        Network net = Discovery.wifi(this);
        if (net == null) {
            viaStatus.setText(Link.NO_WIFI);
            return;
        }
        pcButton.setEnabled(false);
        viaStatus.setText("Looking for PCs running Exo...");
        new Thread(() -> {
            List<Discovery.Answer> found;
            String problem = null;
            try {
                found = Discovery.find(net, null, 1500, l -> false);
            } catch (java.io.IOException e) {
                Log.i(Codecs.TAG, "finding PCs failed: " + e);
                if (Link.blocked(e)) problem = Link.blockedSentence(this);
                found = new ArrayList<>();
            }
            List<Discovery.Answer> pcs = found;
            String why = problem;
            runOnUiThread(() -> {
                pcButton.setEnabled(true);
                bindVia();
                if (why != null) {
                    viaStatus.setText(why);
                    return;
                }
                if (isFinishing()) return;
                List<String> items = new ArrayList<>();
                for (Discovery.Answer a : pcs) items.add(a.name + "  (" + a.address.getHostAddress() + ")");
                items.add("Type its address...");
                new AlertDialog.Builder(this)
                        .setTitle(pcs.isEmpty() ? "No PC answered. Is Exo running on it?" : "Connect to")
                        .setItems(items.toArray(new String[0]), (d, which) -> {
                            if (which < pcs.size()) setVia(new Via(true, pcs.get(which).name, null));
                            else typeAddress();
                        })
                        .show();
            });
        }, "find pcs").start();
    }

    private void typeAddress() {
        EditText field = new EditText(this);
        field.setHint("192.168.1.20");
        field.setInputType(android.text.InputType.TYPE_CLASS_TEXT | android.text.InputType.TYPE_TEXT_VARIATION_URI);
        if (via.ip != null) field.setText(via.ip);
        new AlertDialog.Builder(this)
                .setTitle("The PC's address")
                .setView(field)
                .setPositiveButton("Connect", (d, w) -> {
                    String ip = field.getText().toString().trim();
                    if (!ip.isEmpty()) setVia(new Via(true, null, ip));
                })
                .setNegativeButton("Cancel", null)
                .show();
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
        if (request == ASK_LOCAL_NETWORK) {
            bindVia();
            if (!needsLocalNetwork()) findPcs();
            return;
        }
        if (request != ASK_CAMERA) return;
        if (checkSelfPermission(Manifest.permission.CAMERA) == PackageManager.PERMISSION_GRANTED) {
            useWebcam();
            return;
        }
        webcamProblem = "Exo needs the camera permission to be a webcam. Allow it in Settings, Apps, Exo.";
        refresh();
    }
}
