namespace ExoHub.Display;

// What the display phone chose, from its HELLO (settings are on the phone):
// the monitor's one mode, already turned for portrait; the rate the hub
// encodes at; and the panel's size in mm for the EDID, 0 when the phone
// does not know it. The monitor itself always runs at 60Hz.
readonly record struct DisplayMode(uint Width, uint Height, uint Fps, uint WidthMm, uint HeightMm)
{
    public override string ToString() => $"{Width}x{Height} at {Fps}fps, {WidthMm}x{HeightMm}mm";
}
