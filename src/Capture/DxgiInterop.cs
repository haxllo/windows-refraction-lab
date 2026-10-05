using System.Runtime.InteropServices;
using WinRT;

namespace RefractionLab.Capture;

/// <summary>Gets raw D3D11 interfaces out of Win2D's WinRT objects via IDirect3DDxgiInterfaceAccess.</summary>
internal static class DxgiInterop
{
    public static readonly Guid IidD3D11Device = typeof(Vortice.Direct3D11.ID3D11Device).GUID;
    public static readonly Guid IidD3D11Texture2D = typeof(Vortice.Direct3D11.ID3D11Texture2D).GUID;

    // IDirect3DDxgiInterfaceAccess (windows.graphics.directx.direct3d11.interop.h). Win2D's native DLL embeds this IID.
    private static readonly Guid IidAccess =
        new(0xA9B3D012, 0x3DF2, 0x4EE3, 0xB8, 0xD1, 0x86, 0x95, 0xF4, 0x57, 0xD3, 0xC1);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetInterfaceFn(IntPtr self, ref Guid iid, out IntPtr result);

    /// <summary>Returns an AddRef'd native pointer; the caller owns it (wrap it in a Vortice object).</summary>
    public static IntPtr GetInterface<T>(T winrtObject, Guid iid) where T : class
    {
        IntPtr unknown = MarshalInterface<T>.FromManaged(winrtObject);
        try
        {
            Guid access = IidAccess;
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, ref access, out IntPtr accessPtr));
            try
            {
                IntPtr fn = Marshal.ReadIntPtr(Marshal.ReadIntPtr(accessPtr), 3 * IntPtr.Size); // IUnknown has 3 slots
                var getInterface = Marshal.GetDelegateForFunctionPointer<GetInterfaceFn>(fn);
                Marshal.ThrowExceptionForHR(getInterface(accessPtr, ref iid, out IntPtr result));
                return result;
            }
            finally { Marshal.Release(accessPtr); }
        }
        finally { Marshal.Release(unknown); }
    }
}
