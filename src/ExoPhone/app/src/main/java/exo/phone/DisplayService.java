package exo.phone;

import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.PendingIntent;
import android.app.Service;
import android.content.Context;
import android.content.Intent;
import android.content.pm.ServiceInfo;
import android.os.IBinder;
import android.util.Log;

/**
 * Runs while the display is paused, out of view, and does nothing else: in
 * the foreground, the app keeps its network, which Android cuts from a
 * cached app over Wi-Fi about 5s after it leaves (PLAN.md, v3 step 5). The
 * connection itself stays in DisplayActivity's Link.
 */
public class DisplayService extends Service {
    private static final String CHANNEL = "display";
    private static final int NOTIFICATION = 2;

    /** DisplayActivity paused its link. */
    static void start(Context context) {
        try {
            context.startForegroundService(new Intent(context, DisplayService.class));
        } catch (RuntimeException e) {
            // Android 12 and later refuse from the background. The pause
            // holds anyway over USB; over Wi-Fi the link connects again.
            Log.w(Codecs.TAG, "display service not started: " + e);
        }
    }

    /** Back in view, or the display ended. */
    static void stop(Context context) {
        context.stopService(new Intent(context, DisplayService.class));
    }

    @Override
    public int onStartCommand(Intent intent, int flags, int startId) {
        NotificationManager nm = getSystemService(NotificationManager.class);
        nm.createNotificationChannel(new NotificationChannel(CHANNEL, "Exo display", NotificationManager.IMPORTANCE_LOW));
        // A tap opens Exo where it was: the display, or its screen.
        PendingIntent open = PendingIntent.getActivity(this, 0, getPackageManager().getLaunchIntentForPackage(getPackageName()),
                PendingIntent.FLAG_IMMUTABLE);
        Notification n = new Notification.Builder(this, CHANNEL)
                .setContentTitle("Exo display")
                .setContentText("Still connected to the PC. Open Exo to go back to it.")
                .setSmallIcon(android.R.drawable.ic_menu_view)
                .setContentIntent(open)
                .setOngoing(true)
                .build();
        try {
            startForeground(NOTIFICATION, n, ServiceInfo.FOREGROUND_SERVICE_TYPE_CONNECTED_DEVICE);
            Log.i(Codecs.TAG, "display service in the foreground, type connectedDevice");
        } catch (RuntimeException e) {
            Log.e(Codecs.TAG, "display service startForeground FAILED: " + e);
            stopSelf();
        }
        return START_NOT_STICKY;
    }

    @Override
    public void onDestroy() {
        Log.i(Codecs.TAG, "display service stopped");
        super.onDestroy();
    }

    @Override
    public IBinder onBind(Intent intent) {
        return null;
    }
}
