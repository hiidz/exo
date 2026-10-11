package exo.phone;

import java.nio.ByteBuffer;

/**
 * Splits an Annex-B H.264 byte stream into access units, one coded picture
 * each. Start codes are kept: MediaCodec takes Annex-B as it is.
 *
 * Rule from H.264 7.4.1.2.3: once the current access unit holds a slice,
 * the next AUD, SPS, PPS or SEI starts a new one, as does a slice with
 * first_mb_in_slice == 0. AUDs alone are not enough; they are optional.
 */
final class AnnexB {
    static final int SLICE = 1, IDR = 5, SEI = 6, SPS = 7, PPS = 8, AUD = 9;

    /** One access unit, as a view onto the source buffer. */
    static final class AccessUnit {
        ByteBuffer data;
        boolean idr;
        // SPS and PPS bytes, start codes included, or null when the unit
        // carries none. Assumes they are adjacent, as every file so far has
        // them (AUD, SPS, PPS, IDR).
        ByteBuffer params;
    }

    private final ByteBuffer buf;
    private final int end;
    private int pos;

    AnnexB(ByteBuffer buf) {
        this.buf = buf;
        this.end = buf.limit();
        this.pos = nalStart(0);
    }

    /** The next access unit, or null at the end of the stream. */
    AccessUnit next() {
        if (pos >= end) return null;
        AccessUnit au = new AccessUnit();
        int start = pos, paramsStart = -1, paramsEnd = -1;
        boolean haveSlice = false;
        while (pos < end) {
            int header = headerIndex(pos);
            int type = buf.get(header) & 0x1F;
            boolean slice = type == SLICE || type == IDR;
            boolean firstMbZero = header + 1 < end && (buf.get(header + 1) & 0x80) != 0;
            boolean begins = type == AUD || type == SPS || type == PPS || type == SEI
                    || (slice && firstMbZero);
            if (haveSlice && begins) break;

            int next = nalStart(header);
            if (type == SPS || type == PPS) {
                if (paramsStart < 0) paramsStart = pos;
                paramsEnd = next;
            }
            if (type == IDR) au.idr = true;
            if (slice) haveSlice = true;
            pos = next;
        }
        au.data = view(start, pos);
        if (paramsStart >= 0) au.params = view(paramsStart, paramsEnd);
        return au;
    }

    /**
     * Index of the next start code at or after from, counting a 4-byte
     * 00 00 00 01 from its first zero; end if there is none.
     */
    private int nalStart(int from) {
        for (int i = from; i + 2 < end; i++) {
            if (buf.get(i) == 0 && buf.get(i + 1) == 0 && buf.get(i + 2) == 1) {
                return (i > from && buf.get(i - 1) == 0) ? i - 1 : i;
            }
        }
        return end;
    }

    /** Index of the NAL header byte, just past the start code at nal. */
    private int headerIndex(int nal) {
        return buf.get(nal + 2) == 1 ? nal + 3 : nal + 4;
    }

    private ByteBuffer view(int from, int to) {
        ByteBuffer v = buf.duplicate();
        v.limit(to);
        v.position(from);
        return v;
    }
}
