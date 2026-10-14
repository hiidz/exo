using Vortice.Direct3D11;
using Vortice.DXGI;

namespace ExoHub.Display;

// Two COM calls Vortice 3.8 does not expose usefully, made through the
// vtable. A slot is the method's position in the interface and all its
// bases, counted from IUnknown's three.
static unsafe class ComCalls
{
    // IDXGIKeyedMutex::AcquireSync: IUnknown 3, IDXGIObject 4,
    // IDXGIDeviceSubObject 1, then AcquireSync. Vortice's wrapper returns
    // nothing, which hides WAIT_TIMEOUT (0x102), a success code.
    const int AcquireSyncSlot = 8;

    public static int AcquireSync(IDXGIKeyedMutex mutex, ulong key, uint ms)
    {
        IntPtr self = mutex.NativePointer;
        void** vtable = *(void***)self;
        return ((delegate* unmanaged[Stdcall]<IntPtr, ulong, uint, int>)vtable[AcquireSyncSlot])(self, key, ms);
    }

    // ID3D11Device1::OpenSharedResourceByName: IUnknown 3, ID3D11Device 40,
    // then GetImmediateContext1, CreateDeferredContext1, CreateBlendState1,
    // CreateRasterizerState1, CreateDeviceContextState, OpenSharedResource1.
    // Vortice keeps its wrapper internal.
    const int OpenByNameSlot = 49;

    public static int OpenSharedResourceByName(ID3D11Device1 device, string name, uint access, Guid iid, out IntPtr resource)
    {
        IntPtr self = device.NativePointer;
        void** vtable = *(void***)self;
        IntPtr result = IntPtr.Zero;
        int hr;
        fixed (char* n = name)
            hr = ((delegate* unmanaged[Stdcall]<IntPtr, char*, uint, Guid*, IntPtr*, int>)vtable[OpenByNameSlot])(
                self, n, access, &iid, &result);
        resource = result;
        return hr;
    }
}
