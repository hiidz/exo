package exo.phone;

import android.content.Context;
import android.net.ConnectivityManager;
import android.net.Network;
import android.net.NetworkCapabilities;

import java.io.IOException;
import java.net.DatagramPacket;
import java.net.DatagramSocket;
import java.net.InetAddress;
import java.net.SocketTimeoutException;
import java.nio.ByteBuffer;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.List;
import java.util.function.Predicate;

/**
 * Finds hubs on the Wi-Fi network: a QUERY broadcast, or sent to a typed
 * address, and each hub's ANSWER back to this socket. Layout in the hub's
 * Wire.cs. Every socket is bound to the Wi-Fi network, so mobile data or a
 * Wi-Fi without internet cannot route it elsewhere.
 */
final class Discovery {
    static final int PORT = 27185, VERSION = 1, QUERY = 1, ANSWER = 2;

    static final class Answer {
        final InetAddress address;
        final int port;
        final long session;
        final String name;

        Answer(InetAddress address, int port, long session, String name) {
            this.address = address;
            this.port = port;
            this.session = session;
            this.name = name;
        }
    }

    // Android 17's runtime permission for LAN sockets; earlier versions
    // do not define it, and need nothing (RESEARCH.md, Transport, 5).
    static final String LOCAL_NETWORK = "android.permission.ACCESS_LOCAL_NETWORK";

    /** The permission exists here and is not granted. */
    static boolean needsPermission(Context context) {
        try {
            context.getPackageManager().getPermissionInfo(LOCAL_NETWORK, 0);
        } catch (android.content.pm.PackageManager.NameNotFoundException e) {
            return false;
        }
        return context.checkSelfPermission(LOCAL_NETWORK) != android.content.pm.PackageManager.PERMISSION_GRANTED;
    }

    /** The Wi-Fi network, or null when the phone is on none. */
    static Network wifi(Context context) {
        ConnectivityManager cm = context.getSystemService(ConnectivityManager.class);
        for (Network n : cm.getAllNetworks()) {
            NetworkCapabilities c = cm.getNetworkCapabilities(n);
            if (c != null && c.hasTransport(NetworkCapabilities.TRANSPORT_WIFI)) return n;
        }
        return null;
    }

    /**
     * Asks to (null: everyone on the network) and gathers answers for up to
     * waitMs, one per hub, returning early once enough accepts the list.
     */
    static List<Answer> find(Network net, InetAddress to, int waitMs, Predicate<List<Answer>> enough)
            throws IOException {
        List<Answer> found = new ArrayList<>();
        try (DatagramSocket s = new DatagramSocket()) {
            net.bindSocket(s);
            s.setBroadcast(true);
            byte[] q = ByteBuffer.allocate(7).put(Link.MAGIC).putShort((short) VERSION).put((byte) QUERY).array();
            InetAddress dest = to != null ? to : InetAddress.getByName("255.255.255.255");
            s.send(new DatagramPacket(q, q.length, dest, PORT));
            long end = System.nanoTime() + waitMs * 1_000_000L;
            byte[] buf = new byte[256];
            while (true) {
                int left = (int) ((end - System.nanoTime()) / 1_000_000L);
                if (left <= 0) break;
                s.setSoTimeout(left);
                DatagramPacket p = new DatagramPacket(buf, buf.length);
                try {
                    s.receive(p);
                } catch (SocketTimeoutException e) {
                    break;
                }
                Answer a = parse(p);
                if (a == null) continue;
                boolean dup = false;
                for (Answer f : found) dup |= f.address.equals(a.address) && f.port == a.port;
                if (dup) continue;
                found.add(a);
                if (enough.test(found)) break;
            }
        }
        return found;
    }

    private static Answer parse(DatagramPacket p) {
        ByteBuffer b = ByteBuffer.wrap(p.getData(), 0, p.getLength());
        if (b.remaining() < 18) return null;
        byte[] magic = new byte[4];
        b.get(magic);
        if (!Arrays.equals(magic, Link.MAGIC) || (b.getShort() & 0xffff) != VERSION || b.get() != ANSWER) return null;
        int port = b.getShort() & 0xffff;
        long session = b.getLong();
        int n = b.get() & 0xff;
        if (b.remaining() < n) return null;
        byte[] name = new byte[n];
        b.get(name);
        return new Answer(p.getAddress(), port, session, new String(name, StandardCharsets.UTF_8));
    }

    private Discovery() {
    }
}
