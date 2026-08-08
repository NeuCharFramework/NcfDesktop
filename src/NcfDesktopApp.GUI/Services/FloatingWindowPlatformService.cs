using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using AppKit;
using ObjCRuntime;

namespace NcfDesktopApp.GUI.Services;

/// <summary>
/// 为无边框浮窗补齐平台原生透明和命中区域行为。
/// Avalonia 的透明画刷只控制绘制；原生窗口默认仍是矩形，会拦截透明像素后的点击。
/// </summary>
internal static class FloatingWindowPlatformService
{
    private const int DwmWindowCornerPreference = 33;
    private const int DwmBorderColor = 34;
    private const int DwmCornerDoNotRound = 1;
    private const int DwmColorNone = unchecked((int)0xFFFFFFFE);
    private const int RegionOr = 2;

    public static void ConfigureTransparentWindow(Window window)
    {
        window.Background = Brushes.Transparent;
        window.SystemDecorations = SystemDecorations.None;
        window.TransparencyLevelHint = [WindowTransparencyLevel.Transparent];

        if (OperatingSystem.IsWindows())
        {
            ConfigureWindowsWindow(window);
        }
        else if (OperatingSystem.IsMacOS())
        {
            ConfigureMacOsWindow(window);
        }
    }

    public static void ApplyHitRegions(Window window, IReadOnlyList<FloatingWindowHitRegion> regions)
    {
        if (!OperatingSystem.IsWindows() ||
            window.TryGetPlatformHandle() is not { Handle: var windowHandle } ||
            windowHandle == IntPtr.Zero)
        {
            return;
        }

        var scaling = window.RenderScaling > 0 ? window.RenderScaling : 1;
        var combinedRegion = CreateRectRgn(0, 0, 0, 0);
        if (combinedRegion == IntPtr.Zero)
        {
            return;
        }

        try
        {
            foreach (var region in regions)
            {
                var nativeRegion = CreateNativeRegion(region, scaling);
                if (nativeRegion == IntPtr.Zero)
                {
                    continue;
                }

                try
                {
                    _ = CombineRgn(combinedRegion, combinedRegion, nativeRegion, RegionOr);
                }
                finally
                {
                    _ = DeleteObject(nativeRegion);
                }
            }

            // 成功后区域所有权交给系统，不能再由调用方释放。
            if (SetWindowRgn(windowHandle, combinedRegion, redraw: true) != 0)
            {
                combinedRegion = IntPtr.Zero;
            }
        }
        catch (DllNotFoundException)
        {
            // 非标准兼容层不提供 Win32 API 时继续使用 Avalonia 的矩形窗口。
        }
        catch (EntryPointNotFoundException)
        {
            // 同上。
        }
        finally
        {
            if (combinedRegion != IntPtr.Zero)
            {
                _ = DeleteObject(combinedRegion);
            }
        }
    }

    public static void SetMacOsMousePassthrough(Window window, bool passthrough)
    {
        if (!OperatingSystem.IsMacOS() || !TryGetNativeWindow(window, out var nativeWindow))
        {
            return;
        }

        try
        {
            nativeWindow.IgnoresMouseEvents = passthrough;
        }
        catch (Exception)
        {
            // Avalonia Native 以外的 macOS 后端允许安全降级。
        }
    }

    private static void ConfigureWindowsWindow(Window window)
    {
        if (window.TryGetPlatformHandle() is not { Handle: var windowHandle } ||
            windowHandle == IntPtr.Zero)
        {
            return;
        }

        try
        {
            var cornerPreference = DwmCornerDoNotRound;
            _ = DwmSetWindowAttribute(
                windowHandle,
                DwmWindowCornerPreference,
                ref cornerPreference,
                sizeof(int));

            var borderColor = DwmColorNone;
            _ = DwmSetWindowAttribute(
                windowHandle,
                DwmBorderColor,
                ref borderColor,
                sizeof(int));
        }
        catch (DllNotFoundException)
        {
            // Windows 版本不支持 DWM 属性时，SystemDecorations=None 仍然生效。
        }
        catch (EntryPointNotFoundException)
        {
            // 同上。
        }
    }

    private static void ConfigureMacOsWindow(Window window)
    {
        if (!TryGetNativeWindow(window, out var nativeWindow))
        {
            return;
        }

        try
        {
            nativeWindow.IsOpaque = false;
            nativeWindow.HasShadow = false;
            nativeWindow.BackgroundColor = NSColor.Clear;
        }
        catch (Exception)
        {
            // Avalonia Native 以外的 macOS 后端允许安全降级。
        }
    }

    private static bool TryGetNativeWindow(Window window, out NSWindow nativeWindow)
    {
        var platformHandle = window.TryGetPlatformHandle();
        nativeWindow = null!;
        if (platformHandle?.Handle is not { } handle ||
            handle == IntPtr.Zero ||
            !string.Equals(platformHandle.HandleDescriptor, "NSWindow", StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            nativeWindow = Runtime.GetNSObjectTx<NSWindow>(handle)!;
            return nativeWindow != null;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static IntPtr CreateNativeRegion(FloatingWindowHitRegion region, double scaling)
    {
        var left = (int)Math.Floor(region.Bounds.Left * scaling);
        var top = (int)Math.Floor(region.Bounds.Top * scaling);
        var right = (int)Math.Ceiling(region.Bounds.Right * scaling);
        var bottom = (int)Math.Ceiling(region.Bounds.Bottom * scaling);
        if (right <= left || bottom <= top)
        {
            return IntPtr.Zero;
        }

        if (region.IsEllipse)
        {
            return CreateEllipticRgn(left, top, right, bottom);
        }

        var diameter = Math.Max(1, (int)Math.Ceiling(region.CornerRadius * 2 * scaling));
        return CreateRoundRectRgn(left, top, right, bottom, diameter, diameter);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr windowHandle,
        int attribute,
        ref int attributeValue,
        int attributeSize);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateEllipticRgn(int left, int top, int right, int bottom);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(
        int left,
        int top,
        int right,
        int bottom,
        int ellipseWidth,
        int ellipseHeight);

    [DllImport("gdi32.dll")]
    private static extern int CombineRgn(
        IntPtr destination,
        IntPtr source1,
        IntPtr source2,
        int combineMode);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr windowHandle, IntPtr region, bool redraw);

}

internal readonly record struct FloatingWindowHitRegion(
    Rect Bounds,
    double CornerRadius,
    bool IsEllipse = false)
{
    public bool Contains(Point point)
    {
        if (!Bounds.Contains(point))
        {
            return false;
        }

        if (IsEllipse)
        {
            var radiusX = Bounds.Width / 2;
            var radiusY = Bounds.Height / 2;
            if (radiusX <= 0 || radiusY <= 0)
            {
                return false;
            }

            var normalizedX = (point.X - Bounds.Center.X) / radiusX;
            var normalizedY = (point.Y - Bounds.Center.Y) / radiusY;
            return normalizedX * normalizedX + normalizedY * normalizedY <= 1;
        }

        var radius = Math.Min(CornerRadius, Math.Min(Bounds.Width, Bounds.Height) / 2);
        if (radius <= 0 ||
            (point.X >= Bounds.Left + radius && point.X <= Bounds.Right - radius) ||
            (point.Y >= Bounds.Top + radius && point.Y <= Bounds.Bottom - radius))
        {
            return true;
        }

        var cornerCenterX = point.X < Bounds.Left + radius
            ? Bounds.Left + radius
            : Bounds.Right - radius;
        var cornerCenterY = point.Y < Bounds.Top + radius
            ? Bounds.Top + radius
            : Bounds.Bottom - radius;
        var deltaX = point.X - cornerCenterX;
        var deltaY = point.Y - cornerCenterY;
        return deltaX * deltaX + deltaY * deltaY <= radius * radius;
    }
}
