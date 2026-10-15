package exo.phone;

import android.media.MediaCodecInfo;
import android.media.MediaCodecInfo.CodecCapabilities;
import android.media.MediaCodecInfo.CodecProfileLevel;
import android.media.MediaCodecInfo.VideoCapabilities;
import android.media.MediaCodecList;
import android.media.MediaFormat;
import android.os.Build;
import android.util.Log;

import java.util.List;

/**
 * Logs what MediaCodecList says about every AVC decoder on this device: the
 * per-device decoder check DECISIONS 11.2 and TECHNICAL_DESIGN 4.2 ask for.
 * Measures only; nothing here changes how decoding is done.
 */
final class Codecs {
    static final String TAG = "ExoPhone";
    static final String AVC = MediaFormat.MIMETYPE_VIDEO_AVC;

    /** What Decoder configures with, and what choosing a decoder is checked against. fps 0 leaves the rate out. */
    static MediaFormat decoderFormat(int w, int h, int fps, boolean lowLatency) {
        MediaFormat f = MediaFormat.createVideoFormat(AVC, w, h);
        if (fps > 0) f.setInteger(MediaFormat.KEY_FRAME_RATE, fps);
        if (lowLatency && Build.VERSION.SDK_INT >= 30) f.setInteger(MediaFormat.KEY_LOW_LATENCY, 1);
        return f;
    }

    /**
     * The decoder Decoder uses for this format, or null if none can take it.
     * A given name wins, as in Decoder, but must take the format too.
     * KEY_LOW_LATENCY does not steer findDecoderForFormat: on the TB375FC it
     * picks c2.mtk.avc.decoder over the separate c2.mtk.avc.decoder.lowlatency.
     */
    static String chooseDecoder(MediaFormat format, String name) {
        MediaCodecList list = new MediaCodecList(MediaCodecList.REGULAR_CODECS);
        if (name == null) return list.findDecoderForFormat(format);
        for (MediaCodecInfo info : list.getCodecInfos()) {
            if (!info.isEncoder() && info.getName().equals(name) && supports(info, AVC)) {
                return info.getCapabilitiesForType(AVC).isFormatSupported(format) ? name : null;
            }
        }
        return null;
    }

    /**
     * The highest frame rate the decoder info claims for w x h, as
     * SuperDisplay sets operating-rate (RESEARCH.md, How others do it); 0
     * when it does not say or takes no such size.
     */
    static int maxRate(MediaCodecInfo info, int w, int h) {
        try {
            VideoCapabilities v = info.getCapabilitiesForType(AVC).getVideoCapabilities();
            return (int) Math.floor(v.getSupportedFrameRatesFor(w, h).getUpper());
        } catch (IllegalArgumentException | NullPointerException e) {
            return 0;
        }
    }

    /** sizes is {width, height} pairs; each is checked at every rate. */
    static void logAvcDecoders(int[][] sizes, int[] rates) {
        MediaCodecList list = new MediaCodecList(MediaCodecList.REGULAR_CODECS);
        for (MediaCodecInfo info : list.getCodecInfos()) {
            if (info.isEncoder() || !supports(info, AVC)) continue;

            CodecCapabilities caps = info.getCapabilitiesForType(AVC);
            VideoCapabilities v = caps.getVideoCapabilities();
            Log.i(TAG, String.format("decoder %s canonical=%s hw=%b softwareOnly=%b vendor=%b alias=%b",
                    info.getName(), info.getCanonicalName(), info.isHardwareAccelerated(),
                    info.isSoftwareOnly(), info.isVendor(), info.isAlias()));
            Log.i(TAG, String.format("  widths %s heights %s alignment %dx%d maxInstances %d",
                    v.getSupportedWidths(), v.getSupportedHeights(),
                    v.getWidthAlignment(), v.getHeightAlignment(), caps.getMaxSupportedInstances()));

            String lowLatency = Build.VERSION.SDK_INT >= 30
                    ? String.valueOf(caps.isFeatureSupported(CodecCapabilities.FEATURE_LowLatency))
                    : "n/a below API 30";
            Log.i(TAG, "  feature low-latency " + lowLatency
                    + " adaptive-playback " + caps.isFeatureSupported(CodecCapabilities.FEATURE_AdaptivePlayback));

            // The list holds one entry per profile, each with the highest
            // level the decoder supports at that profile.
            for (CodecProfileLevel pl : caps.profileLevels) {
                Log.i(TAG, "  profile " + profileName(pl.profile) + " max level " + levelName(pl.level));
            }

            for (int[] s : sizes) {
                int w = s[0], h = s[1];
                StringBuilder sb = new StringBuilder();
                for (int fps : rates) {
                    sb.append(' ').append(fps).append("fps=").append(v.areSizeAndRateSupported(w, h, fps));
                }
                Log.i(TAG, String.format("  %dx%d areSizeAndRateSupported%s", w, h, sb));
                try {
                    // Theoretical: derived from the declared limits.
                    Log.i(TAG, "    supported rates " + v.getSupportedFrameRatesFor(w, h));
                    // Measured by the vendor. Null means no measurement, not
                    // "unsupported".
                    Log.i(TAG, "    achievable rates " + v.getAchievableFrameRatesFor(w, h));
                } catch (IllegalArgumentException e) {
                    Log.i(TAG, "    size unsupported: " + e.getMessage());
                }
            }

            List<VideoCapabilities.PerformancePoint> points = v.getSupportedPerformancePoints();
            Log.i(TAG, "  performance points " + points);
        }
    }

    private static boolean supports(MediaCodecInfo info, String mime) {
        for (String t : info.getSupportedTypes()) {
            if (t.equalsIgnoreCase(mime)) return true;
        }
        return false;
    }

    private static String profileName(int p) {
        switch (p) {
            case CodecProfileLevel.AVCProfileBaseline: return "Baseline";
            case CodecProfileLevel.AVCProfileConstrainedBaseline: return "ConstrainedBaseline";
            case CodecProfileLevel.AVCProfileMain: return "Main";
            case CodecProfileLevel.AVCProfileExtended: return "Extended";
            case CodecProfileLevel.AVCProfileHigh: return "High";
            case CodecProfileLevel.AVCProfileConstrainedHigh: return "ConstrainedHigh";
            case CodecProfileLevel.AVCProfileHigh10: return "High10";
            case CodecProfileLevel.AVCProfileHigh422: return "High422";
            case CodecProfileLevel.AVCProfileHigh444: return "High444";
            default: return "0x" + Integer.toHexString(p);
        }
    }

    // AVC level constants are one bit each, in ascending order.
    private static final int[] LEVEL_BITS = {
            CodecProfileLevel.AVCLevel1, CodecProfileLevel.AVCLevel1b, CodecProfileLevel.AVCLevel11,
            CodecProfileLevel.AVCLevel12, CodecProfileLevel.AVCLevel13, CodecProfileLevel.AVCLevel2,
            CodecProfileLevel.AVCLevel21, CodecProfileLevel.AVCLevel22, CodecProfileLevel.AVCLevel3,
            CodecProfileLevel.AVCLevel31, CodecProfileLevel.AVCLevel32, CodecProfileLevel.AVCLevel4,
            CodecProfileLevel.AVCLevel41, CodecProfileLevel.AVCLevel42, CodecProfileLevel.AVCLevel5,
            CodecProfileLevel.AVCLevel51, CodecProfileLevel.AVCLevel52, CodecProfileLevel.AVCLevel6,
            CodecProfileLevel.AVCLevel61, CodecProfileLevel.AVCLevel62,
    };
    private static final String[] LEVEL_NAMES = {
            "1", "1b", "1.1", "1.2", "1.3", "2", "2.1", "2.2", "3", "3.1", "3.2",
            "4", "4.1", "4.2", "5", "5.1", "5.2", "6", "6.1", "6.2",
    };

    private static String levelName(int l) {
        for (int i = 0; i < LEVEL_BITS.length; i++) {
            if (LEVEL_BITS[i] == l) return LEVEL_NAMES[i];
        }
        return "0x" + Integer.toHexString(l);
    }
}
