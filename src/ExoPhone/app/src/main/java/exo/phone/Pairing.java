package exo.phone;

import android.content.Context;
import android.security.keystore.KeyGenParameterSpec;
import android.security.keystore.KeyProperties;

import java.io.IOException;
import java.net.Socket;
import java.nio.ByteBuffer;
import java.security.GeneralSecurityException;
import java.security.KeyPairGenerator;
import java.security.KeyStore;
import java.security.MessageDigest;
import java.security.Principal;
import java.security.PrivateKey;
import java.security.SecureRandom;
import java.security.cert.CertificateException;
import java.security.cert.X509Certificate;
import java.security.spec.ECGenParameterSpec;
import java.util.Arrays;
import java.nio.charset.StandardCharsets;

import javax.net.ssl.KeyManager;
import javax.net.ssl.SSLContext;
import javax.net.ssl.SSLEngine;
import javax.net.ssl.SSLSocket;
import javax.net.ssl.TrustManager;
import javax.net.ssl.X509ExtendedKeyManager;
import javax.net.ssl.X509ExtendedTrustManager;
import javax.security.auth.x500.X500Principal;

/**
 * Wi-Fi's TLS and its pairing, the phone's half of the hub's Pairing.cs. This
 * phone shows its own certificate, whose key Android Keystore made and keeps
 * until the app's data is cleared, and pins the PC's certificate once the PC's
 * user allowed it after comparing the code. Until then it accepts any, since
 * the code is what proves the PC. A pinned PC showing another certificate is
 * refused until the user picks the PC again, which forgets the pin: else a
 * stranger could make it pair again whenever he liked.
 */
final class Pairing {
    private static final String ALIAS = "exo-tls";
    private static final byte[] LABEL = "Exo pairing 1".getBytes(StandardCharsets.US_ASCII);
    static final int NONCE_SIZE = 32;

    /** The PC's certificate as this connection's handshake showed it. */
    static final class HubTrust extends X509ExtendedTrustManager {
        private final String pin;
        X509Certificate hub;
        boolean wrong;  // pinned, and this is not that PC

        HubTrust(String pin) {
            this.pin = pin;
        }

        private void check(X509Certificate[] chain) throws CertificateException {
            if (chain == null || chain.length == 0) throw new CertificateException("the PC showed no certificate");
            if (pin != null && !pin.equals(fingerprint(chain[0]))) {
                wrong = true;
                throw new CertificateException("not the PC this phone paired with");
            }
            hub = chain[0];
        }

        @Override public void checkServerTrusted(X509Certificate[] chain, String authType) throws CertificateException { check(chain); }
        @Override public void checkServerTrusted(X509Certificate[] chain, String authType, Socket s) throws CertificateException { check(chain); }
        @Override public void checkServerTrusted(X509Certificate[] chain, String authType, SSLEngine e) throws CertificateException { check(chain); }
        @Override public void checkClientTrusted(X509Certificate[] chain, String authType) throws CertificateException { throw new CertificateException("client only"); }
        @Override public void checkClientTrusted(X509Certificate[] chain, String authType, Socket s) throws CertificateException { throw new CertificateException("client only"); }
        @Override public void checkClientTrusted(X509Certificate[] chain, String authType, SSLEngine e) throws CertificateException { throw new CertificateException("client only"); }
        @Override public X509Certificate[] getAcceptedIssuers() { return new X509Certificate[0]; }
    }

    /**
     * This phone's key and certificate, made on first use. DIGEST_NONE as
     * well, since the TLS stack hands the key a digest it made itself.
     */
    private static synchronized KeyStore.PrivateKeyEntry mine() throws GeneralSecurityException, IOException {
        KeyStore ks = KeyStore.getInstance("AndroidKeyStore");
        ks.load(null);
        if (!ks.containsAlias(ALIAS)) {
            KeyPairGenerator g = KeyPairGenerator.getInstance(KeyProperties.KEY_ALGORITHM_EC, "AndroidKeyStore");
            g.initialize(new KeyGenParameterSpec.Builder(ALIAS, KeyProperties.PURPOSE_SIGN)
                    .setAlgorithmParameterSpec(new ECGenParameterSpec("secp256r1"))
                    .setDigests(KeyProperties.DIGEST_NONE, KeyProperties.DIGEST_SHA256)
                    .setCertificateSubject(new X500Principal("CN=Exo phone"))
                    .build());
            g.generateKeyPair();
        }
        return (KeyStore.PrivateKeyEntry) ks.getEntry(ALIAS, null);
    }

    static X509Certificate certificate() throws IOException {
        try {
            return (X509Certificate) mine().getCertificate();
        } catch (GeneralSecurityException e) {
            throw new IOException("no key for TLS: " + e, e);
        }
    }

    /**
     * Starts TLS over a connected socket, as the client, with this phone's
     * certificate. Closing the result closes the socket.
     */
    static SSLSocket wrap(Socket t, String host, int port, HubTrust trust) throws IOException {
        try {
            KeyStore.PrivateKeyEntry me = mine();
            X509Certificate[] chain = {(X509Certificate) me.getCertificate()};
            PrivateKey key = me.getPrivateKey();
            // Always this one: the hub asks for a client certificate without
            // naming an issuer this phone would know.
            X509ExtendedKeyManager km = new X509ExtendedKeyManager() {
                @Override public String chooseClientAlias(String[] keyType, Principal[] issuers, Socket s) { return ALIAS; }
                @Override public String chooseEngineClientAlias(String[] keyType, Principal[] issuers, SSLEngine e) { return ALIAS; }
                @Override public String[] getClientAliases(String keyType, Principal[] issuers) { return new String[] {ALIAS}; }
                @Override public X509Certificate[] getCertificateChain(String alias) { return chain; }
                @Override public PrivateKey getPrivateKey(String alias) { return key; }
                @Override public String[] getServerAliases(String keyType, Principal[] issuers) { return null; }
                @Override public String chooseServerAlias(String keyType, Principal[] issuers, Socket s) { return null; }
            };
            SSLContext ctx = SSLContext.getInstance("TLS");
            ctx.init(new KeyManager[] {km}, new TrustManager[] {trust}, null);
            SSLSocket ssl = (SSLSocket) ctx.getSocketFactory().createSocket(t, host, port, true);
            // Its reads keep their own timeout, not the plain socket's.
            ssl.setSoTimeout(t.getSoTimeout());
            ssl.startHandshake();
            return ssl;
        } catch (GeneralSecurityException e) {
            throw new IOException("TLS setup failed: " + e, e);
        }
    }

    static String fingerprint(X509Certificate c) throws CertificateException {
        return Identity.hex(sha256(c.getEncoded()));
    }

    static byte[] sha256(byte[] b) {
        try {
            return MessageDigest.getInstance("SHA-256").digest(b);
        } catch (GeneralSecurityException e) {
            throw new IllegalStateException(e);
        }
    }

    /** The code both screens show, as the hub's Pairing.Code makes it. */
    static String code(X509Certificate hub, X509Certificate phone, byte[] phoneNonce, byte[] hubNonce)
            throws CertificateException {
        try {
            MessageDigest d = MessageDigest.getInstance("SHA-256");
            d.update(LABEL);
            d.update(sha256(hub.getEncoded()));
            d.update(sha256(phone.getEncoded()));
            d.update(phoneNonce);
            d.update(hubNonce);
            long n = (ByteBuffer.wrap(d.digest()).getInt() & 0xFFFFFFFFL) % 1_000_000;
            // A no-break space, so a narrow screen never splits the code.
            return String.format("%03d %03d", n / 1000, n % 1000);
        } catch (java.security.NoSuchAlgorithmException e) {
            throw new IllegalStateException(e);
        }
    }

    /** The pinned PC's fingerprint for target, the PC as Via names it; null before pairing. */
    static String pin(Context context, String target) {
        return context.getSharedPreferences("exo", Context.MODE_PRIVATE).getString("pin " + target, null);
    }

    static void pin(Context context, String target, String fingerprint) {
        context.getSharedPreferences("exo", Context.MODE_PRIVATE).edit().putString("pin " + target, fingerprint).apply();
    }

    /** The user picked target again: the next connection pairs. */
    static void forget(Context context, String target) {
        context.getSharedPreferences("exo", Context.MODE_PRIVATE).edit().remove("pin " + target).apply();
    }

    /**
     * One pairing, shared by both roles and by retries, so each shows the
     * same code: the nonce this phone committed to, and the hub's nonce once
     * heard. A hub nonce never changes within one, since this nonce was
     * revealed with the first: a stranger in the middle could otherwise pick
     * one to make the codes match. A different one starts a new pairing.
     */
    static final class Attempt {
        final String target;
        final byte[] nonce = new byte[NONCE_SIZE];
        final byte[] commitment;
        private byte[] hubNonce;

        private Attempt(String target) {
            this.target = target;
            new SecureRandom().nextBytes(nonce);
            commitment = sha256(nonce);
        }

        /** False when hubNonce differs from the one this pairing already took. */
        synchronized boolean hubNonce(byte[] n) {
            if (hubNonce == null) hubNonce = n.clone();
            return Arrays.equals(hubNonce, n);
        }
    }

    private static Attempt attempt;

    static synchronized Attempt attempt(String target) {
        if (attempt == null || !attempt.target.equals(target)) attempt = new Attempt(target);
        return attempt;
    }

    /** The pairing ended, allowed or denied, or must start again. */
    static synchronized void end(Attempt a) {
        if (attempt == a) attempt = null;
    }

    private Pairing() {
    }
}
