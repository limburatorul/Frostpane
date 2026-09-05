using System.Runtime.InteropServices;

namespace Frostpane.Interop;

[StructLayout(LayoutKind.Sequential)]
internal struct SampleDesc
{
    public uint Count, Quality;
}

[StructLayout(LayoutKind.Sequential)]
internal struct Texture2DDesc
{
    public uint Width, Height, MipLevels, ArraySize;
    public int Format;
    public SampleDesc SampleDesc;
    public int Usage;
    public uint BindFlags, CpuAccessFlags, MiscFlags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MappedSubresource
{
    public IntPtr Data;
    public uint RowPitch, DepthPitch;
}

/// <summary>Creates a GraphicsCaptureItem for a plain HWND, which the WinRT surface cannot do.</summary>
[ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IGraphicsCaptureItemInterop
{
    [PreserveSig] int CreateForWindow(IntPtr window, ref Guid iid, out IntPtr result);
    [PreserveSig] int CreateForMonitor(IntPtr monitor, ref Guid iid, out IntPtr result);
}

/// <summary>
/// The Windows 11 addition that turns off the yellow capture border. It is reached through the
/// ABI rather than the projection so the app can keep targeting the 19041 SDK.
/// </summary>
[ComImport, Guid("f2cdd966-22ae-5ea1-9596-3a289344c3be"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IGraphicsCaptureSession3
{
    // IInspectable
    [PreserveSig] int GetIids(out uint count, out IntPtr iids);
    [PreserveSig] int GetRuntimeClassName(out IntPtr name);
    [PreserveSig] int GetTrustLevel(out int level);

    [PreserveSig] int get_IsBorderRequired([MarshalAs(UnmanagedType.U1)] out bool value);
    [PreserveSig] int put_IsBorderRequired([MarshalAs(UnmanagedType.U1)] bool value);
}

/// <summary>
/// Direct3D 11 reached through raw vtable calls.
///
/// A runtime-callable wrapper is no use here: the device is created on the UI thread but the
/// capture delivers frames on a pool thread, and D3D11 objects do not marshal between COM
/// apartments — every call from the wrong apartment fails with E_NOINTERFACE. Calling the vtable
/// directly sidesteps apartments entirely, which is what these objects expect anyway.
/// </summary>
internal static unsafe class D3D11
{
    public const int DriverTypeHardware = 1;
    public const uint CreateDeviceBgraSupport = 0x20;
    public const uint SdkVersion = 7;

    public const int FormatB8G8R8A8Unorm = 87;

    public const int UsageDefault = 0;
    public const int UsageStaging = 3;

    public const uint BindShaderResource = 0x8;
    public const uint BindRenderTarget = 0x20;
    public const uint CpuAccessRead = 0x20000;
    public const uint MiscGenerateMips = 0x1;
    public const uint MapRead = 1;

    public static Guid IID_ID3D11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
    public static Guid IID_IGraphicsCaptureItem = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    public static Guid IID_IDXGIDevice = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");

    // ID3D11Device: IUnknown occupies slots 0-2.
    private const int DeviceCreateTexture2D = 5;
    private const int DeviceCreateShaderResourceView = 7;

    // ID3D11DeviceContext: IUnknown 0-2, then ID3D11DeviceChild 3-6.
    private const int ContextMap = 14;
    private const int ContextUnmap = 15;
    private const int ContextCopySubresourceRegion = 46;
    private const int ContextGenerateMips = 54;

    // IDirect3DDxgiInterfaceAccess::GetInterface, the only method after IUnknown.
    private const int DxgiAccessGetInterface = 3;

    [DllImport("d3d11.dll")]
    public static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags,
                                               IntPtr featureLevels, uint featureLevelCount, uint sdkVersion,
                                               out IntPtr device, out int featureLevel, out IntPtr context);

    [DllImport("d3d11.dll")]
    public static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    [DllImport("combase.dll", CharSet = CharSet.Unicode)]
    public static extern int WindowsCreateString(string source, int length, out IntPtr hstring);

    [DllImport("combase.dll")]
    public static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("combase.dll")]
    public static extern int RoGetActivationFactory(IntPtr classId, ref Guid iid, out IntPtr factory);

    private static void* Slot(IntPtr comObject, int index) => (*(void***)comObject)[index];

    public static int CreateTexture2D(IntPtr device, ref Texture2DDesc desc, out IntPtr texture)
    {
        IntPtr result;
        int hr;
        fixed (Texture2DDesc* pDesc = &desc)
        {
            hr = ((delegate* unmanaged[Stdcall]<IntPtr, Texture2DDesc*, IntPtr, IntPtr*, int>)
                  Slot(device, DeviceCreateTexture2D))(device, pDesc, IntPtr.Zero, &result);
        }
        texture = result;
        return hr;
    }

    public static int CreateShaderResourceView(IntPtr device, IntPtr resource, out IntPtr view)
    {
        IntPtr result;
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, IntPtr*, int>)
                  Slot(device, DeviceCreateShaderResourceView))(device, resource, IntPtr.Zero, &result);
        view = result;
        return hr;
    }

    public static int Map(IntPtr context, IntPtr resource, uint subresource, uint mapType, out MappedSubresource mapped)
    {
        MappedSubresource result;
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, uint, uint, MappedSubresource*, int>)
                  Slot(context, ContextMap))(context, resource, subresource, mapType, 0, &result);
        mapped = result;
        return hr;
    }

    public static void Unmap(IntPtr context, IntPtr resource, uint subresource) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, void>)
         Slot(context, ContextUnmap))(context, resource, subresource);

    public static void CopySubresourceRegion(IntPtr context, IntPtr destination, uint destSubresource,
                                             IntPtr source, uint sourceSubresource) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, uint, uint, uint, IntPtr, uint, IntPtr, void>)
         Slot(context, ContextCopySubresourceRegion))(context, destination, destSubresource, 0, 0, 0,
                                                      source, sourceSubresource, IntPtr.Zero);

    public static void GenerateMips(IntPtr context, IntPtr shaderResourceView) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, void>)
         Slot(context, ContextGenerateMips))(context, shaderResourceView);

    public static int GetDxgiInterface(IntPtr access, ref Guid iid, out IntPtr result)
    {
        IntPtr value;
        int hr;
        fixed (Guid* pIid = &iid)
        {
            hr = ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)
                  Slot(access, DxgiAccessGetInterface))(access, pIid, &value);
        }
        result = value;
        return hr;
    }
}
