using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BetterGhub.Core;

namespace BetterGhub.Views;

/// <summary>Application icons from the Windows shell, cached per path.</summary>
internal static class ShellIcons
{
    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly string DesktopIcon = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");

    /// <summary>Icon element for a profile: a monitor glyph for Desktop, else the app icon (or a fallback glyph).</summary>
    public static FrameworkElement Element(MouseProfile profile, double size)
    {
        ImageSource? image = profile.IsDesktop ? null : For(profile.ApplicationPath);
        if (image is not null)
        {
            Image picture = new() { Source = image, Width = size, Height = size };
            RenderOptions.SetBitmapScalingMode(picture, BitmapScalingMode.HighQuality);
            return picture;
        }
        return Ui.Glyph(profile.IsDesktop ? "\uE7F4" : "\uE71D", size * 0.8, "Accent").With(new Thickness(0));
    }

    public static ImageSource? For(MouseProfile profile) => For(profile.IsDesktop ? DesktopIcon : profile.ApplicationPath);

    public static ImageSource? For(string path)
    {
        if (Cache.TryGetValue(path, out ImageSource? cached)) return cached;
        ImageSource? image = null;
        try
        {
            FileInfoData info = new();
            // SHGFI_ICON | SHGFI_LARGEICON, with SHGFI_USEFILEATTRIBUTES when the file is missing.
            uint flags = 0x100u | (File.Exists(path) ? 0u : 0x10u);
            if (SHGetFileInfo(path, 0x80, ref info, (uint)Marshal.SizeOf<FileInfoData>(), flags) != nint.Zero && info.Icon != nint.Zero)
            {
                image = Imaging.CreateBitmapSourceFromHIcon(info.Icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                image.Freeze();
                DestroyIcon(info.Icon);
            }
        }
        catch (Exception) { /* No icon is fine. */ }
        return Cache[path] = image;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct FileInfoData
    {
        public nint Icon;
        public int IconIndex;
        public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern nint SHGetFileInfo(string path, uint attributes, ref FileInfoData info, uint size, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint icon);
}
