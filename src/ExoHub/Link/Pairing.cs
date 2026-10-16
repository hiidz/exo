// Wi-Fi's TLS and its pairing. The hub
// is the TLS server with its own self-signed certificate, made once and kept
// in HKCU; the phone shows its own as the client's. A phone that holds no pin
// for this hub, or that this hub has not allowed with the certificate it now
// shows, pairs: both sides show a 6-digit code over both certificates and
// two nonces, and the PC's user allows only if the two match. The phone
// commits to its nonce in HELLO before it sees the hub's, so a man in the
// middle, who cannot change either nonce once the other side has seen it,
// gets one guess in a million per pairing, not as many as he can make keys.

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace ExoHub.Link;

static class Pairing
{
    static readonly byte[] Label = "Exo pairing 1"u8.ToArray();

    // The hub's certificate, made on first use. Loaded from PKCS#12 without
    // EphemeralKeySet: SChannel cannot use an ephemeral key.
    public static X509Certificate2 Certificate { get; } = Load();

    static X509Certificate2 Load()
    {
        byte[] pfx = Ui.Settings.Certificate;
        if (pfx == null)
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var req = new CertificateRequest($"CN=Exo on {Environment.MachineName}", key, HashAlgorithmName.SHA256);
            var now = DateTimeOffset.UtcNow;
            using var made = req.CreateSelfSigned(now.AddDays(-1), now.AddYears(100));
            pfx = made.Export(X509ContentType.Pkcs12);
            Ui.Settings.Certificate = pfx;
        }
        return X509CertificateLoader.LoadPkcs12(pfx, null);
    }

    // What each side pins: SHA-256 of the certificate as sent, hex.
    public static string Fingerprint(X509Certificate c) => Convert.ToHexString(SHA256.HashData(c.GetRawCertData()));

    // The code both screens show, 000000 to 999999, as "123 456". Phone's
    // Link.code computes the same.
    public static string Code(X509Certificate hub, X509Certificate phone, ReadOnlySpan<byte> phoneNonce,
                              ReadOnlySpan<byte> hubNonce)
    {
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        h.AppendData(Label);
        h.AppendData(SHA256.HashData(hub.GetRawCertData()));
        h.AppendData(SHA256.HashData(phone.GetRawCertData()));
        h.AppendData(phoneNonce);
        h.AppendData(hubNonce);
        uint n = BinaryPrimitives.ReadUInt32BigEndian(h.GetHashAndReset()) % 1_000_000;
        return $"{n / 1000:D3} {n % 1000:D3}";
    }
}
