using Vortice.Direct3D11;
using Vortice.Direct3D11.Debug;

namespace ExoHub.Display;

// Reads what the D3D11 debug layer has to say. Spike 5: the layer turned a
// wrong guess about bind flags into a one-line answer.
//
// The layer does not print to the console. It stores messages in an
// ID3D11InfoQueue on the device, which exists only when the device was
// created with DeviceCreationFlags.Debug. Without it every call here is a
// no-op, so callers do not need to know whether the switch is on.
static class D3DDebug
{
    // Prints every stored message and clears the queue. Returns how many
    // there were. Called after calls that succeeded too, not only in failure
    // paths: a warning does not fail the call that caused it.
    public static int Drain(ID3D11Device device, string when)
    {
        using var queue = device.QueryInterfaceOrNull<ID3D11InfoQueue>();
        if (queue == null)
            return 0;

        ulong count = queue.NumStoredMessages;
        for (ulong i = 0; i < count; i++)
        {
            Message m = queue.GetMessage(i);
            Console.WriteLine($"  d3d11 {when}: {m.Severity} {m.Id}: {m.Description}");
        }
        queue.ClearStoredMessages();
        return (int)count;
    }
}
