package exo.phone;

import android.content.Context;
import android.content.SharedPreferences;

/**
 * How this phone reaches the PC, picked on its home screen and kept (PLAN.md,
 * Wi-Fi as built in step 4): USB, through adb reverse, or Wi-Fi to one PC,
 * found by the name its hub answers with, or at an address the user typed.
 */
final class Via {
    final boolean wifi;
    final String pc;  // the PC's name from its answer; null with a typed address
    final String ip;  // typed; null when found by name

    Via(boolean wifi, String pc, String ip) {
        this.wifi = wifi;
        this.pc = pc;
        this.ip = ip;
    }

    static Via load(Context context) {
        SharedPreferences p = context.getSharedPreferences("exo", Context.MODE_PRIVATE);
        return new Via(p.getBoolean("wifi", false), p.getString("pc", null), p.getString("ip", null));
    }

    void save(Context context) {
        context.getSharedPreferences("exo", Context.MODE_PRIVATE).edit()
                .putBoolean("wifi", wifi).putString("pc", pc).putString("ip", ip).apply();
    }

    /** The PC as the screen names it, or null when none is picked. */
    String target() {
        return ip != null ? ip : pc;
    }

    @Override
    public String toString() {
        return wifi ? "Wi-Fi to " + target() : "USB";
    }
}
