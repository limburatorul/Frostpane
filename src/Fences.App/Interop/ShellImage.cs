using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Fences.Interop;

[StructLayout(LayoutKind.Sequential)]
internal struct SIZE
{
    public int cx, cy;
    public SIZE(int cx, int cy) { this.cx = cx; this.cy = cy; }
}

[ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellItemImageFactory
{
    [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr phbm);
}

/// <summary>Produces the same icons and thumbnails Explorer shows, for any shell item.</summary>
internal static class ShellImage
{
    /// <summary>SIIGBF_RESIZETOFIT | SIIGBF_BIGGERSIZEOK — sharp at the requested size.</summary>
    private const int SIIGBF_BIGGERSIZEOK = 0x00000001;

    private static Guid IID_IShellItemImageFactory = new("bcc18b79-ba16-442f-80c4-8a59c30c463b");

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string path, IntPtr bindCtx, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out object item);

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType, bmWidth, bmHeight, bmWidthBytes;
        public ushort bmPlanes, bmBitsPixel;
        public IntPtr bmBits;
    }

    [DllImport("gdi32.dll")] private static extern int GetObject(IntPtr h, int size, ref BITMAP bm);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr h);

    /// <summary>
    /// Returns the item's icon, or null if the shell has none for it. The bitmap the shell hands
    /// back is 32-bit premultiplied BGRA, which maps straight onto Pbgra32.
    /// </summary>
    public static ImageSource? Load(string parsingName, int size)
    {
        IntPtr hbitmap = IntPtr.Zero;
        try
        {
            SHCreateItemFromParsingName(parsingName, IntPtr.Zero, ref IID_IShellItemImageFactory, out object obj);
            var factory = (IShellItemImageFactory)obj;
            if (factory.GetImage(new SIZE(size, size), SIIGBF_BIGGERSIZEOK, out hbitmap) != 0 || hbitmap == IntPtr.Zero)
                return null;

            var info = new BITMAP();
            if (GetObject(hbitmap, Marshal.SizeOf<BITMAP>(), ref info) == 0 || info.bmBits == IntPtr.Zero)
                return null;

            var bitmap = BitmapSource.Create(info.bmWidth, info.bmHeight, 96, 96, PixelFormats.Pbgra32, null,
                                             info.bmBits, info.bmHeight * info.bmWidthBytes, info.bmWidthBytes);
            bitmap.Freeze();    // shared across threads and cached
            return bitmap;
        }
        catch (COMException)
        {
            return null;        // deleted between enumeration and drawing, or an unreadable item
        }
        catch (ArgumentException)
        {
            return null;
        }
        finally
        {
            if (hbitmap != IntPtr.Zero) DeleteObject(hbitmap);
        }
    }
}
