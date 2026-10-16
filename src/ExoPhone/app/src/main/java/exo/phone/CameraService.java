package exo.phone;

import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.Service;
import android.content.Context;
import android.content.Intent;
import android.content.pm.ServiceInfo;
import android.hardware.display.DisplayManager;
import android.os.Handler;
import android.os.IBinder;
import android.os.Looper;
import android.os.PowerManager;
import android.util.Log;
import android.view.Display;

import java.io.File;
import java.io.IOException;

/**
 * The camera role as a foreground service of type camera, so capture goes on
 * with the screen off. Android lets such a service use the camera only if it
 * was started while the app was visible, which is why MainActivity starts it,
 * from "Use as webcam".
 *
 * A partial wake lock keeps the CPU running with the screen off.
 * The service runs until MainActivity stops it or the app is force-stopped.
 * Its status, the link's sentence, is in the notification and on
 * MainActivity's screen.
 */
public class CameraService extends Service {
    private static final String CHANNEL = "camera";
    private static final int NOTIFICATION = 1;
    static final String CONNECTED = "Connected. The PC has a webcam called Exo.";

    // For MainActivity, which shows them. Main thread writes running; the
    // link thread writes status, then posts onChange to the main thread.
    static volatile boolean running;
    static volatile String status;
    static volatile Runnable onChange;

    private Link link;
    private PowerManager.WakeLock wakeLock;
    private volatile int lastTurns;  // the last landscape HELLO rotation; link thread writes
    // When this webcam first read a landscape way, 0 before. A fixed turn
    // locks lastTurns SETTLE_NS after it: started from a dark screen, the S21
    // reads the other landscape for about 0.35s first (turned).
    private volatile long firstReadNs;
    private static final long SETTLE_NS = 2_000_000_000L;
    private DisplayManager displays;
    private volatile CameraRole current;    // the connection's camera, or null
    private static volatile CameraService instance;

    /**
     * MainActivity changed and saved the webcam's settings. Zoom, focus,
     * exposure and torch go into the running capture; another lens, side,
     * size or rate ends this camera, and the link's next connection opens
     * the new one, its HELLO telling the hub the new size and rate.
     * Main thread.
     */
    static void settingsChanged(Context context) {
        CameraService service = instance;
        CameraRole camera = service == null ? null : service.current;
        if (camera == null) return;
        CameraSettings s = CameraSettings.load(context);
        String opens = CameraRole.opens(s);
        // Fixed holds the way it lies now; back to Auto, it follows the phone.
        int turns = service.lastTurns;
        if (!service.locked(s)) {
            try {
                turns = service.turns(s);
            } catch (IOException e) {
                Log.w(Codecs.TAG, "camera rotation on a settings change: " + e);
            }
        }
        if (opens != null && !opens.equals(camera.opened())) {
            camera.restart("the webcam changed to " + opens);
        } else if (turns != service.lastTurns) {
            camera.restart("the webcam's turn changed, hub turns " + turns * 90);
        } else {
            camera.update(s);
        }
    }

    @Override
    public int onStartCommand(Intent intent, int flags, int startId) {
        if (link != null) {
            Log.i(Codecs.TAG, "camera service already running; start ignored");
            return START_NOT_STICKY;
        }
        NotificationManager nm = getSystemService(NotificationManager.class);
        nm.createNotificationChannel(new NotificationChannel(CHANNEL, "Exo camera", NotificationManager.IMPORTANCE_LOW));
        try {
            startForeground(NOTIFICATION, notification(Link.WAITING), ServiceInfo.FOREGROUND_SERVICE_TYPE_CAMERA);
        } catch (RuntimeException e) {
            // Android 12 and later throw if the app was not allowed to start
            // a foreground service from where it was; Android 14 if the camera
            // type was not allowed. Either way there is no camera role.
            Log.e(Codecs.TAG, "camera service startForeground FAILED: " + e);
            setStatus("Android did not let the webcam start: " + e.getMessage());
            stopSelf();
            return START_NOT_STICKY;
        }
        Log.i(Codecs.TAG, "camera service in the foreground, type camera");
        running = true;
        instance = this;
        setStatus(Link.WAITING);

        wakeLock = getSystemService(PowerManager.class)
                .newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "exo:camera");
        wakeLock.acquire();

        File dir = getExternalFilesDir(null);
        String run = String.format("%tH%<tM%<tS-camera", System.currentTimeMillis());
        link = new Link(this, Identity.of(this), intent.getIntExtra("proto", Link.VERSION), Link.ROLE_CAMERA,
                new Link.Listener() {
                    @Override
                    public void onStatus(String sentence) {
                        String s = sentence == null ? CONNECTED : sentence;
                        setStatus(s);
                        nm.notify(NOTIFICATION, notification(s));
                    }

                    @Override
                    public Decoder newDecoder(String conn, java.util.function.LongConsumer acks) {
                        throw new IllegalStateException("camera role has no decoder");
                    }

                    @Override
                    public CameraRole newCamera(String conn) {
                        CameraRole c = new CameraRole(CameraService.this, CameraSettings.load(CameraService.this));
                        current = c;
                        return c;
                    }

                    @Override
                    public File file(String name) {
                        return new File(dir, run + "-" + name);
                    }

                    @Override
                    public byte[] helloTail() {
                        return CameraSettings.load(CameraService.this).helloTail();
                    }

                    @Override
                    public int rotation() throws IOException {
                        CameraSettings s = CameraSettings.load(CameraService.this);
                        if (locked(s)) {
                            Log.i(Codecs.TAG, "camera rotation: fixed, hub turns " + lastTurns * 90);
                        } else {
                            lastTurns = turns(s);
                        }
                        return lastTurns;
                    }
                });
        link.start();
        displays = getSystemService(DisplayManager.class);
        displays.registerDisplayListener(turned, new Handler(Looper.getMainLooper()));
        return START_NOT_STICKY;
    }

    /**
     * HELLO's rotation for settings s. Away from the app, on the home screen
     * or with the screen off, the display reads portrait, a quarter turn the
     * hub refuses, so with Auto the last landscape stands in; before any
     * landscape, 0, which turned() puts right.
     */
    private int turns(CameraSettings s) throws IOException {
        int turns = CameraRole.rotation(this, s);
        if (turns % 2 == 0) {
            if (firstReadNs == 0) firstReadNs = System.nanoTime();
            return turns;
        }
        Log.i(Codecs.TAG, "camera rotation: display not landscape, sending the last landscape, hub turns "
                + lastTurns * 90);
        return lastTurns;
    }

    /**
     * Whether the turn is held at lastTurns: fixed, and settled since this
     * webcam first read a landscape way. Held until the webcam stops, so a
     * reconnect keeps it.
     */
    private boolean locked(CameraSettings s) {
        return s.turn == CameraSettings.TURN_FIXED && firstReadNs != 0 && System.nanoTime() - firstReadNs > SETTLE_NS;
    }

    /**
     * The display turned. If it now faces a landscape way other than the one
     * the hub was sent, the camera connects again with the new rotation.
     * Started from a dark or portrait screen, the S21 shows the app at
     * ROTATION_90 first and 270 some 0.35s later, once its sensor answers,
     * after the first HELLO went out; a phone turned over while streaming
     * lands here too. Main thread.
     */
    private final DisplayManager.DisplayListener turned = new DisplayManager.DisplayListener() {
        private int rotation = -1;

        @Override
        public void onDisplayChanged(int id) {
            if (id != Display.DEFAULT_DISPLAY) return;
            int r = displays.getDisplay(id).getRotation();
            if (r == rotation) return;
            rotation = r;
            CameraRole camera = current;
            if (camera == null || r % 2 == 0) return;
            try {
                CameraSettings s = CameraSettings.load(CameraService.this);
                if (locked(s)) return;
                int turns = turns(s);
                if (turns != lastTurns) camera.restart("the phone turned, hub turns " + turns * 90);
            } catch (IOException e) {
                Log.w(Codecs.TAG, "camera rotation on turning: " + e);
            }
        }

        @Override
        public void onDisplayAdded(int id) {}

        @Override
        public void onDisplayRemoved(int id) {}
    };

    private Notification notification(String text) {
        return new Notification.Builder(this, CHANNEL)
                .setContentTitle("Exo webcam")
                .setContentText(text)
                .setStyle(new Notification.BigTextStyle().bigText(text))
                .setSmallIcon(R.drawable.ic_stat_exo)
                .setOngoing(true)
                .build();
    }

    private static void setStatus(String s) {
        status = s;
        changed();
    }

    private static void changed() {
        Runnable r = onChange;
        if (r != null) r.run();
    }

    @Override
    public void onDestroy() {
        instance = null;
        if (displays != null) displays.unregisterDisplayListener(turned);
        if (link != null) link.stop();
        if (wakeLock != null && wakeLock.isHeld()) wakeLock.release();
        // A failed start keeps its status, so the screen says why.
        if (running) status = null;
        running = false;
        changed();
        Log.i(Codecs.TAG, "camera service destroyed");
        super.onDestroy();
    }

    @Override
    public IBinder onBind(Intent intent) {
        return null;
    }
}
