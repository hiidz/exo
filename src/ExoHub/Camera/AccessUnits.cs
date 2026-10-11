// Splits an Annex-B H.264 file into access units, one per frame, to feed a
// decoder from a recorded stream (DECISIONS 10 item 4, increment C). Only a
// file needs this: on the camera connection each FRAME message is already
// one access unit (5.3, 5.7).
//
// A frame starts at the first slice whose first_mb_in_slice is 0. That is
// the first field of the slice header, Exp-Golomb coded, where 0 is the
// single bit 1: so the top bit of the byte after the NAL header is set.
// SEI, parameter sets and delimiters ahead of a frame's first slice belong
// to that frame.

namespace ExoHub.Camera;

readonly record struct AccessUnit(int Offset, int Length, bool Keyframe, bool Config);

static class AccessUnits
{
    const int Slice = 1, Idr = 5, Sei = 6, Sps = 7, Pps = 8, Delimiter = 9;

    public static List<AccessUnit> Split(byte[] d)
    {
        var units = new List<AccessUnit>();
        int start = -1;           // where the unit being built starts
        bool slice = false, keyframe = false, config = false;
        for (int i = 0; i + 3 < d.Length; i++)
        {
            if (d[i] != 0 || d[i + 1] != 0 || d[i + 2] != 1)
                continue;
            int at = i > 0 && d[i - 1] == 0 ? i - 1 : i;  // a 4-byte start code
            int type = d[i + 3] & 0x1F;
            bool isSlice = type is Slice or Idr;
            bool firstSlice = isSlice && i + 4 < d.Length && (d[i + 4] & 0x80) != 0;
            if (start < 0)
                start = at;
            else if (slice && (firstSlice || type is Sei or Sps or Pps or Delimiter))
            {
                units.Add(new AccessUnit(start, at - start, keyframe, config));
                start = at;
                slice = keyframe = config = false;
            }
            slice |= isSlice;
            keyframe |= type == Idr;
            config |= type is Sps or Pps;
            i += 3;
        }
        if (start >= 0 && slice)
            units.Add(new AccessUnit(start, d.Length - start, keyframe, config));
        return units;
    }
}
