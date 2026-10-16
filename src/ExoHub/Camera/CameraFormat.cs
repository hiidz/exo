namespace ExoHub.Camera;

// The one format the virtual camera offers apps: the size and rate set on
// the camera phone, from its HELLO (settings are on the phone). Windows
// reads it only when the camera is registered, so a new one means
// registering again.
readonly record struct CameraFormat(uint Width, uint Height, uint Fps)
{
    // Before any camera phone has said otherwise; exo_format.h's default.
    public static readonly CameraFormat Default = new(1280, 720, 30);

    public override string ToString() => $"{Width}x{Height} at {Fps}fps";
}
