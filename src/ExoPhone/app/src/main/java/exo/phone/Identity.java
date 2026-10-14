package exo.phone;

import android.content.Context;
import android.content.SharedPreferences;
import android.os.Build;
import android.provider.Settings;

import java.nio.charset.StandardCharsets;
import java.security.SecureRandom;

/**
 * How this phone names itself to the hub: an identity the app makes once,
 * random, and keeps until its data is cleared, and the name the user gave the
 * phone. The hub keys roles and the monitor's EDID on the identity, so
 * Windows keeps this phone's monitor position and scale. The app cannot read
 * its adb serial (API 29 and later), and over Wi-Fi there is none.
 */
final class Identity {
    static final int ID_SIZE = 16, MAX_NAME = 64;

    final byte[] id;
    final String name;

    private Identity(byte[] id, String name) {
        this.id = id;
        this.name = name;
    }

    static Identity of(Context context) {
        SharedPreferences p = context.getSharedPreferences("exo", Context.MODE_PRIVATE);
        String hex = p.getString("id", null);
        byte[] id;
        if (hex == null) {
            id = new byte[ID_SIZE];
            new SecureRandom().nextBytes(id);
            p.edit().putString("id", hex(id)).apply();
        } else {
            id = new byte[ID_SIZE];
            for (int i = 0; i < ID_SIZE; i++) id[i] = (byte) Integer.parseInt(hex.substring(2 * i, 2 * i + 2), 16);
        }
        String name = Settings.Global.getString(context.getContentResolver(), Settings.Global.DEVICE_NAME);
        if (name == null || name.isEmpty()) name = Build.MODEL;
        return new Identity(id, name);
    }

    /** The name as UTF-8, cut to MAX_NAME bytes on a character boundary. */
    byte[] nameBytes() {
        String n = name;
        byte[] b = n.getBytes(StandardCharsets.UTF_8);
        while (b.length > MAX_NAME) {
            n = n.substring(0, n.length() - 1);
            b = n.getBytes(StandardCharsets.UTF_8);
        }
        return b;
    }

    static String hex(byte[] b) {
        StringBuilder sb = new StringBuilder();
        for (byte x : b) sb.append(String.format("%02x", x));
        return sb.toString();
    }
}
