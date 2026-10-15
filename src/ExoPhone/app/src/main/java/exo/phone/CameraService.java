package exo.phone;

import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.Service;
import android.content.Context;
import android.content.Intent;
import android.content.pm.ServiceInfo;
import android.os.IBinder;
import android.os.PowerManager;
import android.util.Log;

import java.io.File;
import java.io.IOException;

/**
 * The camera role as a foreground service of type camera (DECISIONS 10 item
 * 4, D2a), so capture goes on with the screen off. Android lets such a
 * service use the camera only if it was started while the app was visible,
 * which is why MainActivity starts it, from "Use as webcam".
 *
 * A partial wake lock keeps the CPU running with the screen off (traps-phone).
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
    private int lastTurns;  // the last landscape HELLO rotation; link thread only
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
        if (opens != null && !opens.equals(camera.opened())) {
            camera.restart("the webcam changed to " + opens);
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
        link = new Link(Identity.of(this), intent.getIntExtra("proto", Link.VERSION), Link.ROLE_CAMERA,
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
                        // Away from the app, on the home screen or with the
                        // screen off, the display reads portrait, a quarter
                        // turn the hub refuses. The last landscape stands in
                        // (DECISIONS 6.4); the first read is always with the
                        // app in front, in landscape.
                        int turns = CameraRole.rotation(CameraService.this, CameraSettings.load(CameraService.this));
                        if (turns % 2 == 0) {
                            lastTurns = turns;
                            return turns;
                        }
                        Log.i(Codecs.TAG, "camera rotation: display not landscape, sending the last landscape, hub turns "
                                + lastTurns * 90);
                        return lastTurns;
                    }
                });
        link.start();
        return START_NOT_STICKY;
    }

    private Notification notification(String text) {
        return new Notification.Builder(this, CHANNEL)
                .setContentTitle("Exo webcam")
                .setContentText(text)
                .setStyle(new Notification.BigTextStyle().bigText(text))
                .setSmallIcon(android.R.drawable.presence_video_online)
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
