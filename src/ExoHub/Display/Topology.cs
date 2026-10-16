using static ExoHub.Win32;

namespace ExoHub.Display;

// Where the EXO monitor sits on the desktop (PLAN.md, display driver
// decision): extended, never primary, never a clone. Windows extends onto a
// new monitor by itself and remembers where the user put it, so this acts
// only when the monitor is inactive, overlaps another display, or is
// primary. Unelevated SetDisplayConfig with a supplied config works
// (measured 2026-10-14 on VDD).
//
// The monitor is named by what IOCTL_EXO_ADD_MONITOR returned: the driver's
// adapter LUID and the target ID, never by a GDI name.
static class Topology
{
    public enum State
    {
        Missing,   // Windows has no path to the target yet
        Inactive,  // a path exists, but the monitor is not on the desktop
        Fine,      // active, extended, not primary
        Bad,       // active, but primary or overlapping another display
    }

    public static State Check(uint luidLow, int luidHigh, uint target, out string what)
    {
        Query(QDC_ONLY_ACTIVE_PATHS, out var paths, out var modes);
        int exo = Array.FindIndex(paths, p => IsTarget(p, luidLow, luidHigh, target));
        if (exo < 0)
        {
            Query(QDC_ALL_PATHS, out var all, out _);
            bool known = all.Any(p => IsTarget(p, luidLow, luidHigh, target) && p.targetAvailable != 0);
            what = known ? "not on the desktop" : "no path yet";
            return known ? State.Inactive : State.Missing;
        }

        var r = SourceRect(paths[exo], modes);
        var others = OtherSources(paths, modes, exo).ToList();
        what = $"at ({r.x},{r.y}) {r.w}x{r.h}";
        if (others.Count == 0)
        {
            // Nothing else to make primary. Leaving it beats a black screen.
            what += ", the only display";
            return State.Fine;
        }
        if (r.x == 0 && r.y == 0)
        {
            what += ", primary";
            return State.Bad;
        }
        if (others.Any(o => r.x < o.x + o.w && o.x < r.x + r.w && r.y < o.y + o.h && o.y < r.y + r.h))
        {
            what += ", overlapping another display";
            return State.Bad;
        }
        return State.Fine;
    }

    // The monitor's rectangle on the desktop and its refresh rate, while it
    // is active. Where touch lands, so it is read again whenever it may
    // have moved.
    // turns: Windows' Display orientation, clockwise quarter turns, 0 to 3.
    // The source mode keeps the unturned size (2026-10-16, s6-winrot: after
    // Portrait the hub still read 2944x1840), so a quarter turn swaps it here.
    public static bool Placement(uint luidLow, int luidHigh, uint target,
                                 out (int x, int y, int w, int h) rect, out (uint num, uint den) refresh, out int turns)
    {
        Query(QDC_ONLY_ACTIVE_PATHS, out var paths, out var modes);
        int exo = Array.FindIndex(paths, p => IsTarget(p, luidLow, luidHigh, target));
        if (exo < 0)
        {
            rect = default;
            refresh = default;
            turns = 0;
            return false;
        }
        rect = SourceRect(paths[exo], modes);
        refresh = (paths[exo].refreshNumerator, paths[exo].refreshDenominator);
        turns = paths[exo].rotation is >= 1 and <= 4 ? (int)paths[exo].rotation - 1 : 0;
        if (turns % 2 == 1)
            rect = (rect.x, rect.y, rect.h, rect.w);
        return true;
    }

    // Puts the monitor on the desktop, or moves it right of the others with
    // the old primary kept primary. Returns SetDisplayConfig's result, 0 on
    // success, and saves the layout so Windows reuses it.
    public static int Enforce(uint luidLow, int luidHigh, uint target)
    {
        const uint flags = SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_APPLY | SDC_SAVE_TO_DATABASE | SDC_ALLOW_CHANGES;
        Query(QDC_ONLY_ACTIVE_PATHS, out var paths, out var modes);
        int exo = Array.FindIndex(paths, p => IsTarget(p, luidLow, luidHigh, target));

        if (exo < 0)
        {
            // The active paths as they are, plus the monitor's path with no
            // modes, for Windows to fill in: its preferred mode, and a
            // position beside the others.
            Query(QDC_ALL_PATHS, out var all, out _);
            int i = Array.FindIndex(all, p => IsTarget(p, luidLow, luidHigh, target) && p.targetAvailable != 0);
            if (i < 0)
                return -1;
            var path = all[i];
            path.flags |= DISPLAYCONFIG_PATH_ACTIVE;
            path.sourceModeIdx = DISPLAYCONFIG_PATH_MODE_IDX_INVALID;
            path.targetModeIdx = DISPLAYCONFIG_PATH_MODE_IDX_INVALID;
            return SetDisplayConfig((uint)paths.Length + 1, [.. paths, path], (uint)modes.Length, modes, flags);
        }

        // Shift the others so one of them is at (0,0), the one already there
        // if any, then put the monitor right of them all, top aligned.
        var others = OtherSources(paths, modes, exo).ToList();
        if (others.Count == 0)
            return 0;
        var anchor = others.FirstOrDefault(o => o.x == 0 && o.y == 0, others[0]);
        int right = others.Max(o => o.x - anchor.x + o.w);
        uint exoMode = paths[exo].sourceModeIdx;
        for (int m = 0; m < modes.Length; m++)
        {
            if (modes[m].infoType != DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE)
                continue;
            if (m == exoMode)
            {
                modes[m].x = right;
                modes[m].y = 0;
            }
            else
            {
                modes[m].x -= anchor.x;
                modes[m].y -= anchor.y;
            }
        }
        return SetDisplayConfig((uint)paths.Length, paths, (uint)modes.Length, modes, flags);
    }

    static bool IsTarget(DISPLAYCONFIG_PATH_INFO p, uint luidLow, int luidHigh, uint target) =>
        p.targetAdapterLow == luidLow && p.targetAdapterHigh == luidHigh && p.targetId == target;

    static (int x, int y, int w, int h) SourceRect(DISPLAYCONFIG_PATH_INFO p, DISPLAYCONFIG_MODE_INFO[] modes)
    {
        var m = modes[p.sourceModeIdx];
        return (m.x, m.y, (int)m.width, (int)m.height);
    }

    // Every other active path's source rectangle, once per source mode.
    static IEnumerable<(int x, int y, int w, int h)> OtherSources(DISPLAYCONFIG_PATH_INFO[] paths,
                                                                   DISPLAYCONFIG_MODE_INFO[] modes, int exo)
    {
        var seen = new HashSet<uint> { paths[exo].sourceModeIdx };
        foreach (var p in paths)
            if (p.sourceModeIdx != DISPLAYCONFIG_PATH_MODE_IDX_INVALID && seen.Add(p.sourceModeIdx))
                yield return SourceRect(p, modes);
    }

    // The buffer sizes can grow between the two calls when the topology
    // changes, which QueryDisplayConfig reports as a short buffer: ask again.
    static void Query(uint flags, out DISPLAYCONFIG_PATH_INFO[] paths, out DISPLAYCONFIG_MODE_INFO[] modes)
    {
        while (true)
        {
            int r = GetDisplayConfigBufferSizes(flags, out uint pathCount, out uint modeCount);
            if (r != 0)
                throw new InvalidOperationException($"GetDisplayConfigBufferSizes failed {r}");
            paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
            modes = new DISPLAYCONFIG_MODE_INFO[modeCount];
            r = QueryDisplayConfig(flags, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
            if (r == ERROR_INSUFFICIENT_BUFFER)
                continue;
            if (r != 0)
                throw new InvalidOperationException($"QueryDisplayConfig failed {r}");
            Array.Resize(ref paths, (int)pathCount);
            Array.Resize(ref modes, (int)modeCount);
            return;
        }
    }
}
