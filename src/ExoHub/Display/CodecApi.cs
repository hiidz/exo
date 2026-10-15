using System.Runtime.InteropServices;

namespace ExoHub.Display;

// ICodecAPI, reached by raw vtable call. Vortice binds nearly all of Media
// Foundation but not this, and it is where low latency, rate control, GOP
// length and force-keyframe live. The technique is spike 5's CodecApi.cs.
//
// A COM pointer points at a vtable pointer, which points at an array of
// function pointers in header declaration order. A call is: read the array,
// take slot N, call it with the object pointer as a hidden first argument.
// delegate* unmanaged[Stdcall] is a raw function pointer, so the call has no
// allocation and no marshalling layer, and nothing checks it either: a wrong
// slot calls the wrong function. Slots below are from ICodecAPI in strmif.h,
// SDK 10.0.26100 (checked 2026-10-09): IUnknown 0..2, IsSupported 3,
// GetValue 8, SetValue 9.
//
// Owns one reference to the interface for its lifetime, released by Dispose.
sealed unsafe class CodecApi : IDisposable
{
    static readonly Guid IID = new("901db4c7-31ce-41a2-85dc-8fa0bf41b8da");

    // Property keys from codecapi.h.
    public static readonly Guid LowLatencyMode = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
    public static readonly Guid RateControlMode = new("1c0608e9-370c-4710-8a58-cb6181c42423");
    public static readonly Guid Quality = new("fcbf57a3-7ea5-4b0c-9644-69b40c39c391");  // AVEncCommonQuality
    public static readonly Guid GopSize = new("95f31b26-95a4-41aa-9303-246a7fc6eef1");
    public static readonly Guid ForceKeyFrame = new("398c1b98-8353-475a-9ef2-8f265d260345");

    public const ushort VT_BOOL = 11;
    public const ushort VT_UI4 = 19;

    // VARIANT on x64: a 2-byte type tag, padding, the payload at offset 8.
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    struct Variant
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public ulong val;
    }

    [DllImport("oleaut32.dll")]
    static extern int VariantClear(Variant* v);

    IntPtr api;

    // Takes the MFT's own pointer. QueryInterface and Release are the
    // runtime's, so only the ICodecAPI slots are called by hand. Throws if
    // the MFT does not expose ICodecAPI.
    public CodecApi(IntPtr mft)
    {
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(mft, in IID, out api));
    }

    public bool IsSupported(Guid key)
    {
        void** vtbl = *(void***)api;
        return ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, int>)vtbl[3])(api, &key) == 0;
    }

    // Returns the HRESULT. On success, value is the payload and vt its type,
    // so a caller can tell a VT_BOOL true (0xFFFF) from a number.
    public int TryGet(Guid key, out ulong value, out ushort vt)
    {
        var v = default(Variant);
        void** vtbl = *(void***)api;
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, Variant*, int>)vtbl[8])(api, &key, &v);
        value = v.val;
        vt = v.vt;
        VariantClear(&v);  // nothing to free for numbers, but the contract is to clear
        return hr;
    }

    // Throws on a failed HRESULT. VARIANT_BOOL true is -1, so a bool goes
    // in as 0xFFFF, not 1.
    public void Set(Guid key, uint value, ushort vt)
    {
        var v = new Variant { vt = vt, val = vt == VT_BOOL ? (value != 0 ? 0xFFFFUL : 0) : value };
        void** vtbl = *(void***)api;
        Marshal.ThrowExceptionForHR(
            ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, Variant*, int>)vtbl[9])(api, &key, &v));
    }

    // One line for a readback report: supported, then value or HRESULT.
    public string Describe(string name, Guid key)
    {
        int hr = TryGet(key, out ulong value, out ushort vt);
        string got = hr == 0 ? $"{value} (vt {vt})" : $"unreadable 0x{hr:X8}";
        return $"{name,-28} supported={IsSupported(key),-5} value={got}";
    }

    public void Dispose()
    {
        if (api != IntPtr.Zero)
            Marshal.Release(api);
        api = IntPtr.Zero;
    }
}
