package exo.phone;

import android.Manifest;
import android.app.Activity;
import android.app.AlertDialog;
import android.content.Intent;
import android.content.pm.PackageManager;
import android.content.res.TypedArray;
import android.graphics.Color;
import android.net.Network;
import android.os.Build;
import android.os.Bundle;
import android.util.Log;
import android.util.TypedValue;
import android.view.Gravity;
import android.view.View;
import android.view.WindowInsets;
import android.widget.Button;
import android.widget.EditText;
import android.widget.FrameLayout;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.SeekBar;
import android.widget.Switch;
import android.widget.TextView;

import java.util.ArrayList;
import java.util.Arrays;
import java.util.List;

/**
 * Exo's screen on the phone: one page in three sections, Connection,
 * Display and Webcam, each a heading with its state, then its settings as
 * labelled rows whose value opens a choice list.
 * "Use as display" opens DisplayActivity, full screen, until the user
 * leaves it. "Use as webcam" starts CameraService, which goes on with the
 * screen off until the same button stops it, and asks for the camera
 * permission the first time. One phone may do both.
 *
 * The tools pick roles with an extra, through the same paths as the buttons:
 *
 *   adb shell am start -S -n exo.phone/.MainActivity --es role camera,display [--ei proto 3]
 *       [--ei rate 60] [--es size 1800x810]
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
    // A role's state, as the dot before its heading shows it.
    private static final int OFF = Color.GRAY, WAITING = 0xFFE0A030, WORKING = 0xFF3DB060;

    private Via via;
    private TextView viaValue, pcValue, viaStatus;
    private View pcRow;

    private DisplaySettings settings;
    private TextView displayState, sizeNote, rateValue;

    private Button displayButton, stopDisplayButton;

    private CameraSettings camera;
    private TextView webcamState, facingValue, lensValue, camSizeValue, camRateValue, turnValue, focusLabel;
    private View torchRow, focusRow;
    private Switch torchSwitch;
    private SeekBar zoomBar, exposureBar, focusBar;
    private Button webcamButton;
    // A sentence of this screen's own, such as a refused permission, shown
    // in place of the service's until the next start.
    private String webcamProblem;

    private int accent;

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        TypedArray a = obtainStyledAttributes(new int[] {android.R.attr.colorAccent});
        accent = a.getColor(0, 0xFF4080FF);
        a.recycle();

        LinearLayout root = new LinearLayout(this);
        root.setOrientation(LinearLayout.VERTICAL);
        TextView title = new TextView(this);
        title.setText("Exo");
        title.setTextAppearance(android.R.style.TextAppearance_DeviceDefault_Large);
        title.setTextSize(28);
        root.addView(title);
        TextView about = new TextView(this);
        about.setText("Use this phone as a display or a webcam for your PC.");
        about.setPadding(0, dp(4), 0, dp(8));
        root.addView(about);

        // How to reach the PC, first, since both roles use it. A change
        // reaches a role the next time it starts.
        heading(root, "Connection", null);
        via = Via.load(this);
        viaValue = row(root, "Connect by", () -> choose("Connect by", Arrays.asList("USB", "Wi-Fi"), via.wifi ? 1 : 0, i -> {
            boolean wifi = i == 1;
            if (wifi == via.wifi) return;
            setVia(new Via(wifi, via.pc, via.ip));
            if (wifi && via.target() == null) findPcs();
        }));
        pcValue = row(root, "PC", this::findPcs);
        pcRow = (View) pcValue.getParent();
        viaStatus = note(root);
        bindVia();

        // The display. While it runs, out of view, its button goes back to it
        // and a second one ends it. The hub's monitor gets exactly this mode
        // when the display starts, or shows again.
        displayState = heading(root, "Display", null);
        LinearLayout displayRow = new LinearLayout(this);
        displayButton = new Button(this);
        displayButton.setOnClickListener(v -> useDisplay());
        displayRow.addView(displayButton, new LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1));
        stopDisplayButton = new Button(this);
        stopDisplayButton.setText("Stop display");
        stopDisplayButton.setOnClickListener(v -> {
            DisplayActivity.end();
            refresh();
        });
        displayRow.addView(stopDisplayButton, new LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1));
        root.addView(displayRow);
        settings = DisplaySettings.load(this);
        rateValue = row(root, "Frame rate", () -> choose("Frame rate", Arrays.asList("30 fps", "60 fps"),
                settings.fps == 60 ? 1 : 0, i -> displayPicked(DisplaySettings.RATES[i])));
        sizeNote = note(root);
        bindDisplaySettings();

        // The webcam. Zoom, exposure, focus and torch change the picture as
        // they move; side, lens, size and rate reopen the camera, a second's
        // gap, and the hub registers the camera again for a new size or rate,
        // so an app using it must reopen it.
        webcamState = heading(root, "Webcam", null);
        webcamButton = new Button(this);
        webcamButton.setOnClickListener(v -> {
            if (CameraService.running) {
                stopService(new Intent(this, CameraService.class));
            } else {
                useWebcam();
            }
        });
        root.addView(webcamButton);
        camera = CameraSettings.load(this);
        facingValue = row(root, "Camera", () -> {
            if (sides.isEmpty()) return;
            List<String> items = new ArrayList<>();
            for (boolean front : sides) items.add(front ? "Front" : "Back");
            choose("Camera", items, Math.max(0, sides.indexOf(camera.front)), i -> {
                if (sides.get(i) == camera.front) return;
                camera.front = sides.get(i);  // that side's lens is the one last chosen there
                cameraPicked();
            });
        });
        lensValue = row(root, "Lens", () -> {
            List<CameraSettings.Lens> side = camera.side();
            if (side.isEmpty()) return;
            List<String> items = new ArrayList<>();
            for (CameraSettings.Lens l : side) items.add(l.label(side));
            choose("Lens", items, Math.max(0, side.indexOf(camera.lens())), i -> {
                if (side.get(i) == camera.lens()) return;
                camera.chooseLens(side.get(i).id);
                cameraPicked();
            });
        });
        camSizeValue = row(root, "Resolution", () -> {
            CameraSettings.Lens lens = camera.lens();
            if (lens == null || lens.sizes.isEmpty()) return;
            List<String> items = new ArrayList<>();
            for (int[] s : lens.sizes) items.add(s[0] + " x " + s[1]);
            choose("Resolution", items, Math.max(0, lens.sizes.indexOf(camera.size())), i -> {
                if (lens.sizes.get(i) == camera.size()) return;
                camera.width = lens.sizes.get(i)[0];
                camera.height = lens.sizes.get(i)[1];
                cameraPicked();
            });
        });
        camRateValue = row(root, "Frame rate", () -> {
            List<Integer> rates = camRates();
            List<String> items = new ArrayList<>();
            for (int r : rates) items.add(r + " fps");
            choose("Frame rate", items, Math.max(0, rates.indexOf(camera.fps())), i -> {
                if (rates.get(i) == camera.fps()) return;
                camera.fps = rates.get(i);
                cameraPicked();
            });
        });
        turnValue = row(root, "Turn", () -> choose("Turn", Arrays.asList(
                "Auto: follows the phone", "Fixed: holds the way it lay at start"), camera.turn, i -> {
            if (i == camera.turn) return;
            camera.turn = i;
            cameraPicked();
        }));
        torchSwitch = new Switch(this);
        torchSwitch.setOnCheckedChangeListener((s, on) -> {
            if (camera.torch == on) return;
            camera.torch = on;
            cameraChanged();
        });
        torchRow = control(root, "Torch", torchSwitch, false);
        zoomBar = slider(root, "Zoom");
        exposureBar = slider(root, "Exposure");
        focusBar = slider(root, "Focus");
        focusRow = (View) focusBar.getParent();
        focusLabel = (TextView) ((LinearLayout) focusRow).getChildAt(0);
        bindCamera();

        // One column, at most 640dp wide, centred on a wide screen; it
        // scrolls when a short landscape phone cannot fit it all.
        FrameLayout frame = new FrameLayout(this);
        frame.addView(root, new FrameLayout.LayoutParams(
                getResources().getConfiguration().screenWidthDp > 680 ? dp(640) : FrameLayout.LayoutParams.MATCH_PARENT,
                FrameLayout.LayoutParams.WRAP_CONTENT, Gravity.CENTER_HORIZONTAL));
        ScrollView scroll = new ScrollView(this);
        scroll.addView(frame);
        // From Android 15 the window reaches under the system bars, which in
        // landscape put the navigation bar over the left edge (seen on the
        // S21), so the bars' insets are added to the padding.
        scroll.setOnApplyWindowInsetsListener((v, insets) -> {
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
            v.setPadding(dp(24) + l, dp(16) + t, dp(24) + r, dp(24) + b);
            return insets;
        });
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
            bindDisplaySettings();
            useDisplay();
        }
    }

    private int dp(int v) {
        return Math.round(TypedValue.applyDimension(TypedValue.COMPLEX_UNIT_DIP, v, getResources().getDisplayMetrics()));
    }

    /** A section's heading, with a dot and a state after it; returns the state's text. */
    private TextView heading(LinearLayout parent, String text, String state) {
        LinearLayout h = new LinearLayout(this);
        h.setGravity(Gravity.CENTER_VERTICAL);
        h.setPadding(0, dp(24), 0, dp(8));
        TextView t = new TextView(this);
        t.setText(text.toUpperCase());
        t.setTextColor(accent);
        t.setTextSize(13);
        t.setLetterSpacing(0.08f);
        t.setTypeface(android.graphics.Typeface.DEFAULT_BOLD);
        h.addView(t, new LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1));
        TextView s = new TextView(this);
        s.setText(state);
        h.addView(s);
        parent.addView(h);
        View rule = new View(this);
        rule.setBackgroundColor(0x33888888);
        parent.addView(rule, new LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, Math.max(1, dp(1) / 2)));
        return s;
    }

    /** A state after a heading: a coloured dot, then the words. */
    private static void state(TextView t, int colour, String words) {
        android.text.SpannableString s = new android.text.SpannableString("●  " + words);
        s.setSpan(new android.text.style.ForegroundColorSpan(colour), 0, 1, 0);
        t.setText(s);
    }

    /** A setting: its label, then its value, which a tap changes. Returns the value's text. */
    private TextView row(LinearLayout parent, String label, Runnable onTap) {
        TextView value = new TextView(this);
        value.setTextColor(accent);
        value.setGravity(Gravity.END);
        View r = control(parent, label, value, true);
        r.setOnClickListener(v -> onTap.run());
        return value;
    }

    /** A labelled row holding a control on its right. */
    private View control(LinearLayout parent, String label, View right, boolean tappable) {
        LinearLayout r = new LinearLayout(this);
        r.setGravity(Gravity.CENTER_VERTICAL);
        r.setMinimumHeight(dp(48));
        if (tappable) {
            TypedValue bg = new TypedValue();
            getTheme().resolveAttribute(android.R.attr.selectableItemBackground, bg, true);
            r.setBackgroundResource(bg.resourceId);
        }
        TextView l = new TextView(this);
        l.setText(label);
        l.setTextSize(16);
        r.addView(l, new LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1));
        r.addView(right);
        parent.addView(r, new LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT));
        return r;
    }

    /** A sentence under a section's rows. */
    private TextView note(LinearLayout parent) {
        TextView t = new TextView(this);
        t.setPadding(0, dp(4), 0, dp(4));
        t.setAlpha(0.75f);
        parent.addView(t);
        return t;
    }

    private interface Pick {
        void picked(int which);
    }

    /** A choice list, one tap picks and closes it. */
    private void choose(String title, List<String> items, int selected, Pick pick) {
        new AlertDialog.Builder(this)
                .setTitle(title)
                .setSingleChoiceItems(items.toArray(new String[0]), selected, (d, which) -> {
                    d.dismiss();
                    pick.picked(which);
                })
                .show();
    }

    /** A labelled slider on a row of its own. */
    private SeekBar slider(LinearLayout parent, String label) {
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
        View r = control(parent, label, bar, false);
        // The slider takes two thirds of the row.
        ((LinearLayout) r).setWeightSum(3);
        bar.setLayoutParams(new LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 2));
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

    /**
     * A display setting picked: the settings follow it and are kept, and the
     * size is worked out again for the rate, the largest the
     * decoder takes.
     */
    private void displayPicked(int fps) {
        if (fps == settings.fps) return;
        settings.fps = fps;
        settings.refresh(null);
        settings.save(this);
        Log.i(Codecs.TAG, "display settings picked: " + settings);
        bindDisplaySettings();
    }

    private void bindDisplaySettings() {
        sizeNote.setText(settings.size != null ? "The PC's monitor: " + settings.width() + " x " + settings.height()
                + ". For a portrait display, turn the monitor in Windows' display settings, Display orientation."
                : "This phone cannot decode the display at " + settings.fps + " fps. Pick 30 fps.");
        rateValue.setText(settings.fps + " fps");
    }

    // The sides with a lens, in order: false back, true front.
    private final List<Boolean> sides = new ArrayList<>();

    /** The rates this lens offers at this size. */
    private List<Integer> camRates() {
        CameraSettings.Lens lens = camera.lens();
        List<Integer> rates = new ArrayList<>();
        for (int r : CameraSettings.RATES) {
            if (r == 60 && !(lens != null && lens.sizes60.contains(camera.size()))) continue;
            rates.add(r);
        }
        return rates;
    }

    /** The webcam's controls, from camera, each shown only where this lens has it. */
    private void bindCamera() {
        sides.clear();
        for (boolean front : new boolean[] {false, true}) {
            for (CameraSettings.Lens l : camera.lenses) {
                if (l.front != front) continue;
                sides.add(front);
                break;
            }
        }
        facingValue.setText(sides.isEmpty() ? "None" : camera.front ? "Front" : "Back");
        List<CameraSettings.Lens> side = camera.side();
        CameraSettings.Lens lens = camera.lens();
        boolean any = lens != null;
        lensValue.setText(any ? lens.label(side) : "None");
        ((View) lensValue.getParent()).setVisibility(side.size() > 1 ? View.VISIBLE : View.GONE);
        int[] size = camera.size();
        camSizeValue.setText(any && size != null ? size[0] + " x " + size[1] : "None");
        camRateValue.setText(camera.fps() + " fps");
        turnValue.setText(camera.turn == 0 ? "Auto" : "Fixed");
        torchRow.setVisibility(any && lens.flash ? View.VISIBLE : View.GONE);
        torchSwitch.setChecked(camera.torch);
        zoomBar.setMax(100);
        ((View) zoomBar.getParent()).setVisibility(any && lens.maxZoom > 1 ? View.VISIBLE : View.GONE);
        zoomBar.setProgress(any && lens.maxZoom > 1 ? Math.round((camera.zoom - 1) / (lens.maxZoom - 1) * 100) : 0);
        exposureBar.setMax(any ? lens.aeMax - lens.aeMin : 0);
        exposureBar.setProgress(any ? camera.exposure - lens.aeMin : 0);
        focusRow.setVisibility(any && lens.minFocus > 0 ? View.VISIBLE : View.GONE);
        focusBar.setMax(100);
        focusBar.setProgress(Math.round(camera.focus * 100));
        focusLabel.setText(camera.focus == 0 ? "Focus: auto" : "Focus");
    }

    /** A webcam setting picked from a list: kept, sent, and the rows shown again. */
    private void cameraPicked() {
        cameraChanged();
        bindCamera();
    }

    /** Kept, and into the running webcam: live, or by reopening the camera for another lens, size or rate. */
    private void cameraChanged() {
        camera.save(this);
        Log.i(Codecs.TAG, "webcam settings: " + camera);
        CameraService.settingsChanged(this);
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
        boolean display = DisplayActivity.running != null;
        displayButton.setText(display ? "Back to display" : "Use as display");
        stopDisplayButton.setVisibility(display ? View.VISIBLE : View.GONE);
        String ds = DisplayActivity.status;
        if (!display) state(displayState, OFF, "Off");
        else if (ds == null) state(displayState, WORKING, "Connected");
        else state(displayState, WAITING, ds);

        webcamButton.setText(CameraService.running ? "Stop webcam" : "Use as webcam");
        String ws = webcamProblem != null ? webcamProblem : CameraService.status;
        if (webcamProblem != null) state(webcamState, WAITING, webcamProblem);
        else if (!CameraService.running) state(webcamState, OFF, "Off");
        else if (ws == null) state(webcamState, WORKING, "Connected");
        else state(webcamState, WAITING, ws);
    }

    /** The user picked this PC, again or anew: it pairs again, with a code (Pairing). */
    private void pick(Via v) {
        Pairing.forget(this, v.target());
        setVia(v);
    }

    private void setVia(Via v) {
        via = v;
        via.save(this);
        Log.i(Codecs.TAG, "connect by " + via);
        bindVia();
    }

    private void bindVia() {
        viaValue.setText(via.wifi ? "Wi-Fi" : "USB");
        pcRow.setVisibility(via.wifi ? View.VISIBLE : View.GONE);
        pcValue.setText(via.target() == null ? "Pick one" : via.target());
        if (!via.wifi) viaStatus.setText("Plug this phone into the PC, with USB debugging on.");
        else if (needsLocalNetwork()) viaStatus.setText(Link.NEEDS_PERMISSION);
        else viaStatus.setText("On the same Wi-Fi as the PC. The first time, the PC asks to allow this phone, and both show a code to compare.");
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
        pcRow.setEnabled(false);
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
                pcRow.setEnabled(true);
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
                            if (which < pcs.size()) pick(new Via(true, pcs.get(which).name, null));
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
                    if (!ip.isEmpty()) pick(new Via(true, null, ip));
                })
                .setNegativeButton("Cancel", null)
                .show();
    }

    /** Starts the display, or brings the one running back to the front: there is only ever one. */
    private void useDisplay() {
        startActivity(new Intent(this, DisplayActivity.class).putExtras(getIntent())
                .addFlags(Intent.FLAG_ACTIVITY_REORDER_TO_FRONT));
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
