package exo.phone;

import android.Manifest;
import android.app.Activity;
import android.app.AlertDialog;
import android.content.Intent;
import android.content.SharedPreferences;
import android.content.pm.PackageManager;
import android.content.res.ColorStateList;
import android.graphics.Typeface;
import android.graphics.drawable.Drawable;
import android.graphics.drawable.GradientDrawable;
import android.graphics.drawable.RippleDrawable;
import android.net.Network;
import android.os.Build;
import android.os.Bundle;
import android.util.Log;
import android.util.TypedValue;
import android.view.Gravity;
import android.view.View;
import android.view.WindowInsets;
import android.widget.EditText;
import android.widget.FrameLayout;
import android.widget.ImageView;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.SeekBar;
import android.widget.Switch;
import android.widget.TextView;

import java.util.ArrayList;
import java.util.Arrays;
import java.util.List;

/**
 * Exo's screen on the phone: one scrolling column of three cards,
 * Connection, Display and Webcam. Each role's card has its state as a dot
 * and words, then its action. The webcam's live controls show only while
 * it runs; its setup rows fold into a one-line summary that opens in place.
 * Each setting row opens a choice list.
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
    // The design's colours, from res/values, or res/values-night while the
    // system's dark theme is on; read once in onCreate, which a theme
    // change runs again. The state dots are the same in both.
    private int OFF, WAITING, WORKING, SCREEN, CARD, TEXT, SECONDARY, DIVIDER, OUTLINE, SWITCH_OFF, ACCENT, ON_ACCENT,
            TONAL, ON_TONAL, SEGMENT, AMBER, CALLOUT, CALLOUT_TEXT, CODE;
    private static final int FILLED = 0, OUTLINED = 1, TONED = 2;

    private TextView subtitle;

    private Via via;
    private TextView usbSegment, wifiSegment, pcValue, viaStatus;
    private View pcRow;

    private DisplaySettings settings;
    private TextView displayState, sizeNote, rateValue;
    private TextView displayButton, stopDisplayButton;
    private View displayCallout;

    private CameraSettings camera;
    private TextView webcamState, facingValue, lensValue, camSizeValue, camRateValue, turnValue;
    private TextView summary, zoomValue, exposureValue, focusValue, focusLabel;
    private View liveControls, torchRow, zoomRow, exposureRow, focusRow, lensRow, collapsed, expanded, webcamCallout;
    private Switch torchSwitch;
    private SeekBar zoomBar, exposureBar, focusBar;
    private TextView webcamButton;
    // A sentence of this screen's own, such as a refused permission, shown
    // in place of the service's until the next start.
    private String webcamProblem;
    private SharedPreferences ui;

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        ui = getSharedPreferences("ui", MODE_PRIVATE);
        OFF = getColor(R.color.dot_off);
        WAITING = getColor(R.color.dot_waiting);
        WORKING = getColor(R.color.dot_working);
        SCREEN = getColor(R.color.screen);
        CARD = getColor(R.color.card);
        TEXT = getColor(R.color.text);
        SECONDARY = getColor(R.color.text_secondary);
        DIVIDER = getColor(R.color.divider);
        OUTLINE = getColor(R.color.outline);
        SWITCH_OFF = getColor(R.color.outline_strong);
        ACCENT = getColor(R.color.accent);
        ON_ACCENT = getColor(R.color.on_accent);
        TONAL = getColor(R.color.tonal);
        ON_TONAL = getColor(R.color.on_tonal);
        SEGMENT = getColor(R.color.segment_selected);
        AMBER = getColor(R.color.problem);
        CALLOUT = getColor(R.color.callout);
        CALLOUT_TEXT = getColor(R.color.on_callout);
        CODE = getColor(R.color.code);

        LinearLayout root = column(12);

        // The header: the mark, the name, and what the app is for while
        // neither role runs.
        LinearLayout header = column(4);
        header.setPadding(dp(4), dp(8), dp(4), dp(6));
        LinearLayout name = new LinearLayout(this);
        name.setGravity(Gravity.CENTER_VERTICAL);
        ImageView mark = new ImageView(this);
        mark.setImageResource(R.drawable.ic_exo);
        name.addView(mark, new LinearLayout.LayoutParams(dp(26), dp(26)));
        TextView title = text("Exo", 24, TEXT, MEDIUM);
        title.setPadding(dp(10), 0, 0, 0);
        name.addView(title);
        header.addView(name);
        subtitle = text("Use this phone as a display or a webcam for your PC.", 14, SECONDARY, null);
        header.addView(subtitle);
        root.addView(header);

        // How to reach the PC, first, since both roles use it. A change
        // reaches a role the next time it starts.
        LinearLayout connection = card(12);
        LinearLayout by = new LinearLayout(this);
        by.setGravity(Gravity.CENTER_VERTICAL);
        by.addView(label("CONNECT BY"), new LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1));
        LinearLayout segments = new LinearLayout(this);
        segments.setBackground(shape(SCREEN, 12, 0));
        segments.setPadding(dp(3), dp(3), dp(3), dp(3));
        usbSegment = segment("USB", false);
        wifiSegment = segment("Wi-Fi", true);
        segments.addView(usbSegment, new LinearLayout.LayoutParams(dp(76), LinearLayout.LayoutParams.WRAP_CONTENT));
        segments.addView(wifiSegment, new LinearLayout.LayoutParams(dp(76), LinearLayout.LayoutParams.WRAP_CONTENT));
        by.addView(segments);
        connection.addView(by);
        via = Via.load(this);
        pcValue = valueRow(connection, "PC", false, this::findPcs);
        pcRow = (View) pcValue.getParent();
        viaStatus = note();
        connection.addView(viaStatus);
        root.addView(connection);
        bindVia();

        // The display. While it runs, out of view, its button goes back to it
        // and a second one ends it. The hub's monitor gets exactly this mode
        // when the display starts, or shows again.
        LinearLayout display = card(14);
        displayState = heading(display, "Display");
        LinearLayout buttons = new LinearLayout(this);
        displayButton = button("Use as display");
        displayButton.setOnClickListener(v -> useDisplay());
        stopDisplayButton = button("Stop display");
        style(stopDisplayButton, OUTLINED);
        stopDisplayButton.setOnClickListener(v -> {
            DisplayActivity.end();
            refresh();
        });
        buttons.addView(displayButton, new LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1));
        LinearLayout.LayoutParams second = new LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1);
        second.leftMargin = dp(10);
        buttons.addView(stopDisplayButton, second);
        display.addView(buttons);
        displayCallout = callout(display);
        settings = DisplaySettings.load(this);
        rateValue = valueRow(display, "Frame rate", true, () -> choose("Frame rate", Arrays.asList("30 fps", "60 fps"),
                settings.fps == 60 ? 1 : 0, i -> displayPicked(DisplaySettings.RATES[i])));
        sizeNote = note();
        display.addView(sizeNote);
        root.addView(display);
        bindDisplaySettings();

        // The webcam. Zoom, exposure, focus and torch change the picture as
        // they move; side, lens, size and rate reopen the camera, a second's
        // gap, and the hub registers the camera again for a new size or rate,
        // so an app using it must reopen it.
        LinearLayout webcam = card(14);
        webcamState = heading(webcam, "Webcam");
        webcamButton = button("Use as webcam");
        webcamButton.setOnClickListener(v -> {
            if (CameraService.running) {
                stopService(new Intent(this, CameraService.class));
            } else {
                useWebcam();
            }
        });
        webcam.addView(webcamButton);
        webcamCallout = callout(webcam);
        camera = CameraSettings.load(this);

        LinearLayout live = column(2);
        live.setPadding(0, dp(4), 0, 0);
        liveControls = live;
        torchSwitch = new Switch(this);
        int[][] onOff = {{android.R.attr.state_checked}, {}};
        torchSwitch.setThumbTintList(new ColorStateList(onOff, new int[] {ON_ACCENT, SWITCH_OFF}));
        torchSwitch.setTrackTintList(new ColorStateList(onOff, new int[] {ACCENT, SWITCH_OFF}));
        torchSwitch.setOnCheckedChangeListener((s, on) -> {
            if (camera.torch == on) return;
            camera.torch = on;
            cameraChanged();
        });
        LinearLayout torch = new LinearLayout(this);
        torch.setGravity(Gravity.CENTER_VERTICAL);
        torch.setPadding(0, dp(4), 0, dp(4));
        torch.addView(text("Torch", 14, TEXT, null), new LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1));
        torch.addView(torchSwitch);
        live.addView(torch);
        torchRow = torch;
        zoomBar = slider(live, "Zoom");
        zoomRow = (View) zoomBar.getParent();
        zoomValue = (TextView) ((LinearLayout) zoomRow).getChildAt(2);
        exposureBar = slider(live, "Exposure");
        exposureRow = (View) exposureBar.getParent();
        exposureValue = (TextView) ((LinearLayout) exposureRow).getChildAt(2);
        focusBar = slider(live, "Focus");
        focusRow = (View) focusBar.getParent();
        focusLabel = (TextView) ((LinearLayout) focusRow).getChildAt(0);
        focusValue = (TextView) ((LinearLayout) focusRow).getChildAt(2);
        webcam.addView(live);

        // The setup: one line of its values, or, opened, a row each. Open or
        // shut is kept.
        LinearLayout fold = new LinearLayout(this);
        fold.setGravity(Gravity.CENTER_VERTICAL);
        summary = text("", 14, SECONDARY, null);
        summary.setLineSpacing(0, 1.4f);
        fold.addView(summary, new LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1));
        TextView open = text("Settings ˅", 14, ACCENT, MEDIUM);
        open.setPadding(dp(10), 0, 0, 0);
        fold.addView(open);
        fold.setOnClickListener(v -> setExpanded(true));
        collapsed = divided(fold);
        webcam.addView(collapsed);

        LinearLayout rows = column(0);
        LinearLayout settingsHead = new LinearLayout(this);
        settingsHead.setGravity(Gravity.CENTER_VERTICAL);
        settingsHead.setPadding(0, 0, 0, dp(4));
        settingsHead.addView(label("SETTINGS"), new LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1));
        settingsHead.addView(text("Hide ˄", 14, ACCENT, MEDIUM));
        settingsHead.setOnClickListener(v -> setExpanded(false));
        rows.addView(settingsHead);
        facingValue = listRow(rows, "Camera", false, () -> {
            if (sides.isEmpty()) return;
            List<String> items = new ArrayList<>();
            for (boolean front : sides) items.add(front ? "Front" : "Back");
            choose("Camera", items, Math.max(0, sides.indexOf(camera.front)), i -> {
                if (sides.get(i) == camera.front) return;
                camera.front = sides.get(i);  // that side's lens is the one last chosen there
                cameraPicked();
            });
        });
        lensValue = listRow(rows, "Lens", true, () -> {
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
        lensRow = (View) lensValue.getParent().getParent();
        camSizeValue = listRow(rows, "Resolution", true, () -> {
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
        camRateValue = listRow(rows, "Frame rate", true, () -> {
            List<Integer> rates = camRates();
            List<String> items = new ArrayList<>();
            for (int r : rates) items.add(r + " fps");
            choose("Frame rate", items, Math.max(0, rates.indexOf(camera.fps())), i -> {
                if (rates.get(i) == camera.fps()) return;
                camera.fps = rates.get(i);
                cameraPicked();
            });
        });
        turnValue = listRow(rows, "Turn", true, () -> choose("Turn", Arrays.asList(
                "Auto: follows the phone", "Fixed: holds the way it lay at start"), camera.turn, i -> {
            if (i == camera.turn) return;
            camera.turn = i;
            cameraPicked();
        }));
        expanded = divided(rows);
        webcam.addView(expanded);
        root.addView(webcam);
        setExpanded(ui.getBoolean("webcamSettingsExpanded", false));
        bindCamera();

        // One column, at most 640dp wide, centred on a wide screen; it
        // scrolls when a short landscape phone cannot fit it all.
        FrameLayout frame = new FrameLayout(this);
        frame.addView(root, new FrameLayout.LayoutParams(
                getResources().getConfiguration().screenWidthDp > 672 ? dp(640) : FrameLayout.LayoutParams.MATCH_PARENT,
                FrameLayout.LayoutParams.WRAP_CONTENT, Gravity.CENTER_HORIZONTAL));
        ScrollView scroll = new ScrollView(this);
        scroll.setBackgroundColor(SCREEN);
        scroll.addView(frame);
        // From Android 15 the window reaches under the system bars, which in
        // landscape put the navigation bar over the left edge (seen on the
        // S21), so the bars' and cutout's insets are added to the padding.
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
            v.setPadding(dp(16) + l, dp(8) + t, dp(16) + r, dp(28) + b);
            return insets;
        });
        setContentView(scroll);
        // No white scrim behind three-button navigation: the cream shows.
        getWindow().setNavigationBarContrastEnforced(false);

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

    private int dp(float v) {
        return Math.round(TypedValue.applyDimension(TypedValue.COMPLEX_UNIT_DIP, v, getResources().getDisplayMetrics()));
    }

    // Views, as the design draws them.

    private static final Typeface MEDIUM = Typeface.create("sans-serif-medium", Typeface.NORMAL);
    private static final Typeface SEMIBOLD = Typeface.create(Typeface.DEFAULT, 600, false);

    private TextView text(String s, float sp, int colour, Typeface face) {
        TextView t = new TextView(this);
        t.setText(s);
        t.setTextSize(sp);
        t.setTextColor(colour);
        if (face != null) t.setTypeface(face);
        return t;
    }

    /** A small capitalised label, such as CONNECT BY. */
    private TextView label(String s) {
        TextView t = text(s, 12, SECONDARY, SEMIBOLD);
        t.setLetterSpacing(0.1f);
        return t;
    }

    /** A sentence under a card's rows, hidden while empty. */
    private TextView note() {
        TextView t = text("", 13, SECONDARY, null);
        t.setLineSpacing(0, 1.45f);
        return t;
    }

    private void setNote(TextView t, String s, boolean problem) {
        t.setText(s);
        t.setTextColor(problem ? AMBER : SECONDARY);
        t.setVisibility(s == null ? View.GONE : View.VISIBLE);
    }

    /** A vertical stack with gapDp between the children shown. */
    private LinearLayout column(int gapDp) {
        LinearLayout l = new LinearLayout(this);
        l.setOrientation(LinearLayout.VERTICAL);
        if (gapDp > 0) {
            GradientDrawable gap = new GradientDrawable();
            gap.setSize(0, dp(gapDp));
            l.setDividerDrawable(gap);
            l.setShowDividers(LinearLayout.SHOW_DIVIDER_MIDDLE);
        }
        return l;
    }

    private LinearLayout card(int gapDp) {
        LinearLayout c = column(gapDp);
        c.setBackground(shape(CARD, 24, 0));
        c.setPadding(dp(18), dp(18), dp(18), dp(18));
        return c;
    }

    private GradientDrawable shape(int fill, float radiusDp, int stroke) {
        GradientDrawable g = new GradientDrawable();
        g.setColor(fill);
        g.setCornerRadius(dp(radiusDp));
        if (stroke != 0) g.setStroke(Math.max(1, dp(1)), stroke);
        return g;
    }

    /** A background that ripples on a tap, inside the given shape. */
    private Drawable touchable(Drawable bg, float radiusDp) {
        return new RippleDrawable(ColorStateList.valueOf(ACCENT & 0x00FFFFFF | 0x1F000000), bg, shape(CARD, radiusDp, 0));
    }

    /** A card's title, then its state as a dot and words; returns the words. */
    private TextView heading(LinearLayout card, String title) {
        LinearLayout h = column(4);
        h.addView(text(title, 20, TEXT, MEDIUM));
        LinearLayout line = new LinearLayout(this);
        View dot = new View(this);
        LinearLayout.LayoutParams at = new LinearLayout.LayoutParams(dp(8), dp(8));
        at.topMargin = dp(6);
        at.rightMargin = dp(8);
        line.addView(dot, at);
        TextView words = text("", 14, TEXT, null);
        words.setLineSpacing(0, 1.45f);
        line.addView(words);
        h.addView(line);
        card.addView(h);
        return words;
    }

    /** A state: the dot's colour, then the words. */
    private void state(TextView words, int colour, String s) {
        View dot = ((LinearLayout) words.getParent()).getChildAt(0);
        dot.setBackground(new GradientDrawable() {{
            setShape(OVAL);
            setColor(colour);
        }});
        words.setText(s);
    }

    /** The pairing box: its sentence, then the code, large. Hidden until pairing. */
    private View callout(LinearLayout card) {
        LinearLayout c = column(6);
        c.setBackground(shape(CALLOUT, 16, 0));
        c.setPadding(dp(16), dp(14), dp(16), dp(14));
        TextView words = text("", 14, CALLOUT_TEXT, null);
        words.setLineSpacing(0, 1.45f);
        c.addView(words);
        TextView code = text("", 30, CODE, Typeface.create(Typeface.MONOSPACE, 600, false));
        code.setLetterSpacing(0.14f);
        c.addView(code);
        c.setVisibility(View.GONE);
        card.addView(c);
        return c;
    }

    /** A role's state from its sentence: off, pairing (the code in the box), connected, or the sentence. */
    private void roleState(TextView words, View callout, boolean running, boolean connected, String sentence) {
        String[] pairing = running ? Link.splitPairing(sentence) : null;
        callout.setVisibility(pairing != null ? View.VISIBLE : View.GONE);
        if (!running) state(words, OFF, "Off");
        else if (pairing != null) {
            state(words, WAITING, "Pairing");
            ((TextView) ((LinearLayout) callout).getChildAt(0)).setText(pairing[0]);
            ((TextView) ((LinearLayout) callout).getChildAt(1)).setText(pairing[1]);
        } else if (connected) state(words, WORKING, sentence != null ? sentence : "Connected");
        else state(words, WAITING, sentence);
    }

    /** A pill button. */
    private TextView button(String s) {
        TextView b = text(s, 15, ON_ACCENT, MEDIUM);
        b.setGravity(Gravity.CENTER);
        b.setPadding(dp(12), dp(12), dp(12), dp(12));
        style(b, FILLED);
        return b;
    }

    private void style(TextView b, int kind) {
        b.setTextColor(kind == FILLED ? ON_ACCENT : kind == TONED ? ON_TONAL : ACCENT);
        b.setBackground(touchable(kind == FILLED ? shape(ACCENT, 22, 0)
                : kind == TONED ? shape(TONAL, 22, 0) : shape(CARD, 22, OUTLINE), 22));
    }

    /** One side of the USB | Wi-Fi control. */
    private TextView segment(String s, boolean wifi) {
        TextView t = text(s, 14, SECONDARY, MEDIUM);
        t.setGravity(Gravity.CENTER);
        t.setPadding(0, dp(8), 0, dp(8));
        t.setOnClickListener(v -> {
            if (wifi == via.wifi) return;
            setVia(new Via(wifi, via.pc, via.ip));
            if (wifi && via.target() == null) findPcs();
        });
        return t;
    }

    private void select(TextView segment, boolean on) {
        segment.setTextColor(on ? TEXT : SECONDARY);
        segment.setBackground(on ? shape(SEGMENT, 10, 0) : null);
        segment.setElevation(on ? dp(1) : 0);
    }

    /** Content under a divider line, 12dp below it. */
    private LinearLayout divided(View content) {
        LinearLayout l = column(0);
        View rule = new View(this);
        rule.setBackgroundColor(DIVIDER);
        l.addView(rule, new LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, Math.max(1, dp(1))));
        content.setPadding(content.getPaddingLeft(), dp(12), content.getPaddingRight(), content.getPaddingBottom());
        l.addView(content);
        return l;
    }

    /** A label and its value in the accent; a tap changes it. Returns the value. */
    private TextView valueRow(LinearLayout parent, String label, boolean underDivider, Runnable onTap) {
        LinearLayout r = new LinearLayout(this);
        r.setGravity(Gravity.CENTER_VERTICAL);
        r.addView(text(label, 15, TEXT, null), new LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1));
        TextView value = text("", 15, ACCENT, MEDIUM);
        value.setGravity(Gravity.END);
        r.addView(value);
        r.setOnClickListener(v -> onTap.run());
        parent.addView(underDivider ? divided(r) : r);
        return value;
    }

    /** A setting row in the opened setup, 12dp above and below, a line above all but the first. */
    private TextView listRow(LinearLayout parent, String label, boolean line, Runnable onTap) {
        LinearLayout wrap = column(0);
        if (line) {
            View rule = new View(this);
            rule.setBackgroundColor(DIVIDER);
            wrap.addView(rule, new LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, Math.max(1, dp(1))));
        }
        LinearLayout r = new LinearLayout(this);
        r.setGravity(Gravity.CENTER_VERTICAL);
        r.setPadding(0, dp(12), 0, dp(12));
        r.addView(text(label, 15, TEXT, null), new LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1));
        TextView value = text("", 15, ACCENT, MEDIUM);
        r.addView(value);
        r.setOnClickListener(v -> onTap.run());
        wrap.addView(r);
        parent.addView(wrap);
        return value;
    }

    /** A live control: label, track, value, as 86dp | the rest | 40dp. */
    private SeekBar slider(LinearLayout parent, String label) {
        LinearLayout r = new LinearLayout(this);
        r.setGravity(Gravity.CENTER_VERTICAL);
        r.setPadding(0, dp(6), 0, dp(6));
        r.addView(text(label, 14, TEXT, null), new LinearLayout.LayoutParams(dp(86), LinearLayout.LayoutParams.WRAP_CONTENT));
        SeekBar bar = new SeekBar(this);
        bar.setProgressTintList(ColorStateList.valueOf(ACCENT));
        bar.setProgressBackgroundTintList(ColorStateList.valueOf(TONAL));
        bar.setThumbTintList(ColorStateList.valueOf(ACCENT));
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
        LinearLayout.LayoutParams track = new LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1);
        track.leftMargin = dp(10) - bar.getPaddingLeft();
        track.rightMargin = dp(10) - bar.getPaddingRight();
        r.addView(bar, track);
        TextView value = text("", 13, SECONDARY, null);
        value.setGravity(Gravity.END);
        r.addView(value, new LinearLayout.LayoutParams(dp(40), LinearLayout.LayoutParams.WRAP_CONTENT));
        parent.addView(r);
        return bar;
    }

    private void setExpanded(boolean open) {
        collapsed.setVisibility(open ? View.GONE : View.VISIBLE);
        expanded.setVisibility(open ? View.VISIBLE : View.GONE);
        ui.edit().putBoolean("webcamSettingsExpanded", open).apply();
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

    private void slid(SeekBar b, int progress) {
        CameraSettings.Lens lens = camera.lens();
        if (lens == null) return;
        if (b == zoomBar) camera.zoom = 1 + progress / 100f * (lens.maxZoom - 1);
        else if (b == exposureBar) camera.exposure = lens.aeMin + progress;
        else camera.focus = progress / 100f;
        liveValues();
        cameraChanged();
    }

    private void liveValues() {
        zoomValue.setText(String.format(java.util.Locale.ROOT, "%.1fx", camera.zoom));
        exposureValue.setText((camera.exposure > 0 ? "+" : "") + camera.exposure);
        focusValue.setText(camera.focus == 0 ? "" : Math.round(camera.focus * 100) + "%");
        focusLabel.setText(camera.focus == 0 ? "Focus: auto" : "Focus");
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
        refresh();
    }

    private void bindDisplaySettings() {
        String n = settings.size == null ? "This phone cannot decode the display at " + settings.fps + " fps. Pick 30 fps."
                : DisplayActivity.running != null ? "Changes apply when you go back."
                : "PC monitor " + settings.width() + " x " + settings.height()
                + ". For portrait, turn it in Windows' display settings, Display orientation.";
        setNote(sizeNote, n, settings.size == null);
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
        lensRow.setVisibility(side.size() > 1 ? View.VISIBLE : View.GONE);
        int[] size = camera.size();
        camSizeValue.setText(any && size != null ? size[0] + " x " + size[1] : "None");
        camRateValue.setText(camera.fps() + " fps");
        turnValue.setText(camera.turn == 0 ? "Auto" : "Fixed");
        // The setup in one line: the same values, the lens only where there is a choice.
        List<String> parts = new ArrayList<>();
        parts.add(facingValue.getText().toString());
        if (side.size() > 1) parts.add(lensValue.getText().toString());
        parts.add(camSizeValue.getText().toString());
        parts.add(camRateValue.getText().toString());
        parts.add(turnValue.getText().toString());
        summary.setText(String.join(" · ", parts));
        torchRow.setVisibility(any && lens.flash ? View.VISIBLE : View.GONE);
        torchSwitch.setChecked(camera.torch);
        zoomBar.setMax(100);
        zoomRow.setVisibility(any && lens.maxZoom > 1 ? View.VISIBLE : View.GONE);
        zoomBar.setProgress(any && lens.maxZoom > 1 ? Math.round((camera.zoom - 1) / (lens.maxZoom - 1) * 100) : 0);
        exposureRow.setVisibility(any && lens.aeMax > lens.aeMin ? View.VISIBLE : View.GONE);
        exposureBar.setMax(any ? lens.aeMax - lens.aeMin : 0);
        exposureBar.setProgress(any ? camera.exposure - lens.aeMin : 0);
        focusRow.setVisibility(any && lens.minFocus > 0 ? View.VISIBLE : View.GONE);
        focusBar.setMax(100);
        focusBar.setProgress(Math.round(camera.focus * 100));
        liveValues();
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
        style(displayButton, display && DisplayActivity.refused() || settings.size == null ? TONED : FILLED);
        stopDisplayButton.setVisibility(display ? View.VISIBLE : View.GONE);
        String ds = DisplayActivity.status;
        roleState(displayState, displayCallout, display, ds == null, ds);
        bindDisplaySettings();

        boolean webcam = CameraService.running;
        webcamButton.setText(webcam ? "Stop webcam" : "Use as webcam");
        style(webcamButton, webcam ? OUTLINED : webcamProblem != null ? TONED : FILLED);
        String ws = CameraService.status;
        if (webcamProblem != null) {
            webcamCallout.setVisibility(View.GONE);
            state(webcamState, WAITING, webcamProblem);
        } else {
            roleState(webcamState, webcamCallout, webcam, CameraService.CONNECTED.equals(ws), ws);
        }
        liveControls.setVisibility(webcam ? View.VISIBLE : View.GONE);
        subtitle.setVisibility(!display && !webcam ? View.VISIBLE : View.GONE);
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
        select(usbSegment, !via.wifi);
        select(wifiSegment, via.wifi);
        pcRow.setVisibility(via.wifi ? View.VISIBLE : View.GONE);
        pcValue.setText((via.target() == null ? "Pick one" : via.target()) + " ›");
        if (!via.wifi) setNote(viaStatus, "Plug this phone into the PC, with USB debugging on.", false);
        else if (needsLocalNetwork()) setNote(viaStatus, Link.NEEDS_PERMISSION, true);
        else setNote(viaStatus, "On the same Wi-Fi as the PC. The first time, the PC asks to allow this phone, and both show a code to compare.", false);
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
            setNote(viaStatus, Link.NO_WIFI, true);
            return;
        }
        pcRow.setEnabled(false);
        setNote(viaStatus, "Looking for PCs running Exo...", false);
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
                    setNote(viaStatus, why, true);
                    return;
                }
                if (isFinishing()) return;
                pcList(pcs);
            });
        }, "find pcs").start();
    }

    /** Connect to: a PC each, its name over its address, then "Type its address...". */
    private void pcList(List<Discovery.Answer> pcs) {
        LinearLayout list = column(0);
        list.setPadding(0, dp(8), 0, dp(8));
        AlertDialog d = new AlertDialog.Builder(this)
                .setTitle(pcs.isEmpty() ? "No PC answered. Is Exo running on it?" : "Connect to")
                .setView(list)
                .create();
        for (Discovery.Answer a : pcs) {
            LinearLayout item = column(0);
            item.setPadding(dp(24), dp(12), dp(24), dp(12));
            item.addView(text(a.name, 16, TEXT, null));
            item.addView(text(a.address.getHostAddress(), 14, SECONDARY, null));
            item.setBackground(touchable(null, 0));
            item.setOnClickListener(v -> {
                d.dismiss();
                pick(new Via(true, a.name, null));
            });
            list.addView(item);
        }
        if (!pcs.isEmpty()) {
            View rule = new View(this);
            rule.setBackgroundColor(DIVIDER);
            LinearLayout.LayoutParams at = new LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, Math.max(1, dp(1)));
            at.topMargin = at.bottomMargin = dp(8);
            list.addView(rule, at);
        }
        TextView type = text("Type its address...", 16, ACCENT, MEDIUM);
        type.setPadding(dp(24), dp(12), dp(24), dp(12));
        type.setBackground(touchable(null, 0));
        type.setOnClickListener(v -> {
            d.dismiss();
            typeAddress();
        });
        list.addView(type);
        d.show();
    }

    private void typeAddress() {
        EditText field = new EditText(this);
        field.setHint("192.168.1.20");
        field.setTextColor(TEXT);
        field.setHintTextColor(SECONDARY);
        field.setBackground(shape(0, 4, SWITCH_OFF));
        field.setPadding(dp(16), dp(14), dp(16), dp(14));
        field.setInputType(android.text.InputType.TYPE_CLASS_TEXT | android.text.InputType.TYPE_TEXT_VARIATION_URI);
        if (via.ip != null) field.setText(via.ip);
        FrameLayout box = new FrameLayout(this);
        box.setPadding(dp(24), dp(8), dp(24), 0);
        box.addView(field);
        new AlertDialog.Builder(this)
                .setTitle("The PC's address")
                .setView(box)
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
