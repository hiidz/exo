package exo.phone;

import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.Service;
import android.content.Intent;
import android.content.pm.ServiceInfo;
import android.os.IBinder;
import android.os.PowerManager;
import android.util.Log;

import java.io.File;

/**
 * The camera role as a foreground service of type camera (DECISIONS 10 item
 * 4, D2a), so capture goes on with the screen off. Android lets such a
 * service use the camera only if it was started while the app was visible,
 * which is why MainActivity shows itself over the lock screen and starts it.
 *
 * A partial wake lock keeps the CPU running with the screen off (CLAUDE.md).
 * The service runs until the app is force-stopped, which the hub does with
 * am start -S on every launch.
 */
public class CameraService extends Service {
    private static final String CHANNEL = "camera";
    private static final int NOTIFICATION = 1;

    private Link link;
    private PowerManager.WakeLock wakeLock;

    @Override
    public int onStartCommand(Intent intent, int flags, int startId) {
        if (link != null) {
            Log.i(Codecs.TAG, "camera service already running; start ignored");
            return START_NOT_STICKY;
        }
        NotificationManager nm = getSystemService(NotificationManager.class);
        nm.createNotificationChannel(new NotificationChannel(CHANNEL, "Exo camera", NotificationManager.IMPORTANCE_LOW));
        Notification n = new Notification.Builder(this, CHANNEL)
                .setContentTitle("Exo camera")
                .setContentText("Sending the camera to the PC")
                .setSmallIcon(android.R.drawable.presence_video_online)
                .setOngoing(true)
                .build();
        try {
            startForeground(NOTIFICATION, n, ServiceInfo.FOREGROUND_SERVICE_TYPE_CAMERA);
        } catch (RuntimeException e) {
            // Android 12 and later throw if the app was not allowed to start
            // a foreground service from where it was; Android 14 if the camera
            // type was not allowed. Either way there is no camera role.
            Log.e(Codecs.TAG, "camera service startForeground FAILED: " + e);
            stopSelf();
            return START_NOT_STICKY;
        }
        Log.i(Codecs.TAG, "camera service in the foreground, type camera");

        wakeLock = getSystemService(PowerManager.class)
                .newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "exo:camera");
        wakeLock.acquire();

        File dir = getExternalFilesDir(null);
        String run = String.format("%tH%<tM%<tS-camera", System.currentTimeMillis());
        link = new Link(intent.getStringExtra("serial"), intent.getIntExtra("proto", Link.VERSION), Link.ROLE_CAMERA,
                new Link.Listener() {
                    @Override
                    public void onRefused(String reason) {
                        // No screen to show it on; the log has it.
                    }

                    @Override
                    public Decoder newDecoder(String conn) {
                        throw new IllegalStateException("camera role has no decoder");
                    }

                    @Override
                    public CameraRole newCamera(String conn) {
                        return new CameraRole(CameraService.this);
                    }

                    @Override
                    public File file(String name) {
                        return new File(dir, run + "-" + name);
                    }
                });
        link.start();
        return START_NOT_STICKY;
    }

    @Override
    public void onDestroy() {
        if (link != null) link.stop();
        if (wakeLock != null && wakeLock.isHeld()) wakeLock.release();
        Log.i(Codecs.TAG, "camera service destroyed");
        super.onDestroy();
    }

    @Override
    public IBinder onBind(Intent intent) {
        return null;
    }
}
