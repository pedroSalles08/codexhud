using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace CodexHud.App;

internal static class WindowMaterial
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaBorderColor = 34;
    private const int DwmwaSystemBackdropType = 38;
    private const int DwmWindowCornerPreferenceRoundSmall = 3;
    private const int DwmSystemBackdropTransientWindow = 3;
    private const int DwmColorNone = unchecked((int)0xFFFFFFFE);

    internal static bool TryApply(Window window)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621) ||
            SystemParameters.HighContrast ||
            !TransparencyEffectsEnabled())
        {
            return false;
        }

        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            var source = HwndSource.FromHwnd(handle);
            if (source?.CompositionTarget is null)
            {
                return false;
            }

            var enabled = 1;
            var corner = DwmWindowCornerPreferenceRoundSmall;
            var border = DwmColorNone;
            var backdrop = DwmSystemBackdropTransientWindow;

            _ = DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref enabled, sizeof(int));
            _ = DwmSetWindowAttribute(handle, DwmwaWindowCornerPreference, ref corner, sizeof(int));
            _ = DwmSetWindowAttribute(handle, DwmwaBorderColor, ref border, sizeof(int));
            if (DwmSetWindowAttribute(
                    handle,
                    DwmwaSystemBackdropType,
                    ref backdrop,
                    sizeof(int)) < 0)
            {
                return false;
            }

            var margins = new Margins(-1, -1, -1, -1);
            if (DwmExtendFrameIntoClientArea(handle, ref margins) < 0)
            {
                return false;
            }

            source.CompositionTarget.BackgroundColor = Colors.Transparent;
            return true;
        }
        catch (Exception exception) when (
            exception is DllNotFoundException or EntryPointNotFoundException or ExternalException)
        {
            return false;
        }
    }

    private static bool TransparencyEffectsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
            writable: false);
        return key?.GetValue("EnableTransparency") is not int enabled || enabled != 0;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        nint windowHandle,
        int attribute,
        ref int attributeValue,
        int attributeSize);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(
        nint windowHandle,
        ref Margins margins);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct Margins(int Left, int Right, int Top, int Bottom);
}
