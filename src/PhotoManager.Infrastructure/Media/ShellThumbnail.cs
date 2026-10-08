using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace PhotoManager.Infrastructure.Media;

/// <summary>
/// Quadro de um vídeo pelo Shell do Windows (o mesmo que o Explorer mostra), sem instalar codecs nem ffmpeg.
/// Funciona para os formatos que o Windows sabe decodificar; devolve nulo quando não há miniatura disponível.
/// </summary>
public static class ShellThumbnail
{
    [StructLayout(LayoutKind.Sequential)] private struct Size { public int Width, Height; }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(Size size, int flags, out IntPtr bitmap);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName([MarshalAs(UnmanagedType.LPWStr)] string path, IntPtr bindContext, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory item);

    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr handle);

    private const int BiggerSizeOk = 0x1, ThumbnailOnly = 0x8;

    public static BitmapSource? TryGet(string path, int size)
    {
        BitmapSource? result = null;
        // O Shell trabalha em STA; o ThreadPool é MTA.
        var thread = new Thread(() => result = Capture(path, size)) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(20))) return null;
        return result;
    }

    private static BitmapSource? Capture(string path, int size)
    {
        try
        {
            var iid = new Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b");
            SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var factory);
            if (factory.GetImage(new Size { Width = size, Height = size }, ThumbnailOnly | BiggerSizeOk, out var hbitmap) != 0 || hbitmap == IntPtr.Zero) return null;
            try
            {
                var source = Imaging.CreateBitmapSourceFromHBitmap(hbitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                return source;
            }
            finally { DeleteObject(hbitmap); }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException) { return null; }
    }
}
