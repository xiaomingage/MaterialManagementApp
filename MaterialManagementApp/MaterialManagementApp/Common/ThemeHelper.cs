using System;
using MaterialManagementApp.Models;
using MaterialManagementApp.Services;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.ViewManagement;
using WinRT.Interop;

namespace MaterialManagementApp.Common;

/// <summary>
/// 主题应用辅助类：把设置中的主题模式落到界面。
/// 四种模式：
/// 浅色（纯色，禁用毛玻璃）/ 深色（纯色，禁用毛玻璃）/
/// 透明毛玻璃（在基底浅色或深色上启用工作台面板与弹窗的局部亚克力）/
/// 跟随系统（读取系统深浅色并监听切换，毛玻璃同步系统透明效果开关）。
/// Win10 或不支持桌面亚克力的环境自动降级为纯色，程序不报错不崩溃。
/// WinUI 3 的 <see cref="Window"/> 没有 RequestedTheme，主题只能设在窗口内容根元素上。
/// </summary>
public static class ThemeHelper
{
    private static readonly Lazy<UISettings> _uiSettings = new(() => new UISettings());

    private static DesktopAcrylicBackdrop? _backdrop;

    // 最近一次应用模式时的上下文：系统主题/透明开关变化时重新套用
    private static Window? _window;
    private static FrameworkElement? _root;
    private static DispatcherQueue? _dispatcherQueue;
    private static bool _listenerHooked;

    /// <summary>
    /// 把主题模式应用到窗口与根元素，并按需开关毛玻璃背景与卡片表面笔刷。
    /// </summary>
    /// <param name="window">主窗口；用于切换 SystemBackdrop（亚克力开/关）。</param>
    /// <param name="root">窗口内容的根元素；主题设在其 RequestedTheme 上。</param>
    /// <param name="settings">当前设置（读取 ThemeMode / BaseTheme）。</param>
    /// <returns>需要向用户展示的降级提示；无降级时为 null。</returns>
    public static string? ApplyMode(Window? window, FrameworkElement? root, AppSettings settings)
    {
        // 记录上下文，供系统主题/透明开关变化事件回调重新套用
        _window = window;
        _root = root;
        _dispatcherQueue ??= DispatcherQueue.GetForCurrentThread();
        HookSystemThemeListener();

        ElementTheme baseTheme;
        var acrylicOn = false;

        switch (settings.ThemeMode)
        {
            case "Light":
                baseTheme = ElementTheme.Light;
                break;
            case "Dark":
                baseTheme = ElementTheme.Dark;
                break;
            case "Acrylic":
                // 透明毛玻璃：沿用基底浅色/深色，仅追加局部亚克力；不支持时自动降级纯色
                baseTheme = settings.BaseTheme == "Dark" ? ElementTheme.Dark : ElementTheme.Light;
                acrylicOn = DesktopAcrylicController.IsSupported();
                break;
            default:
                // 跟随系统：读取系统深浅色；毛玻璃同步系统透明效果开关
                baseTheme = DetectSystemTheme();
                acrylicOn = DesktopAcrylicController.IsSupported() && IsSystemTransparencyEnabled();
                break;
        }

        // 顺序很关键：先更新表面笔刷，再切换主题。
        // 设置 RequestedTheme 会触发 ThemeResource 全量重新求值，若先切主题后更新笔刷，
        // 卡片/弹窗会取到上一次模式的旧刷子（深色下白卡白字不可见）。
        ApplySurfaceBrush(acrylicOn, baseTheme == ElementTheme.Dark);
        ApplyContentBackground(acrylicOn, baseTheme == ElementTheme.Dark);

        // 主题变化本身会带动 ThemeResource 重求值；若主题没变（如跟随系统下仅开关毛玻璃），
        // 则先反向再设回，强制所有 ThemeResource 引用重新取值。两次赋值同步完成，不产生中间渲染。
        if (root is not null)
        {
            var current = root.RequestedTheme;
            root.RequestedTheme = current == ElementTheme.Dark ? ElementTheme.Light : ElementTheme.Dark;
            root.RequestedTheme = baseTheme;
        }

        ApplyBackdrop(window, acrylicOn);
        ApplyTitleBar(window, baseTheme == ElementTheme.Dark, acrylicOn);
        return ComputeHint(settings);
    }

    /// <summary>计算当前主题模式下的降级提示文案；无降级时为 null。</summary>
    public static string? ComputeHint(AppSettings settings)
    {
        if (settings.ThemeMode != "Acrylic")
        {
            return null;
        }

        if (!DesktopAcrylicController.IsSupported())
        {
            return "当前系统不支持亚克力透明效果";
        }

        if (!IsSystemTransparencyEnabled())
        {
            return "系统已关闭透明效果，已自动切换为纯色显示";
        }

        return null;
    }

    /// <summary>读取 Windows 系统深浅色设置。</summary>
    private static ElementTheme DetectSystemTheme()
    {
        try
        {
            var background = _uiSettings.Value.GetColorValue(UIColorType.Background);
            var luminance = (0.299 * background.R + 0.587 * background.G + 0.114 * background.B) / 255.0;
            return luminance < 0.5 ? ElementTheme.Dark : ElementTheme.Light;
        }
        catch
        {
            return ElementTheme.Light;
        }
    }

    /// <summary>读取系统「透明效果」开关。</summary>
    private static bool IsSystemTransparencyEnabled()
    {
        try
        {
            return _uiSettings.Value.AdvancedEffectsEnabled;
        }
        catch
        {
            // 读取失败按开启处理，交由系统自身降级兜底
            return true;
        }
    }

    /// <summary>订阅系统主题/透明开关变化事件，实现「跟随系统」实时同步。</summary>
    private static void HookSystemThemeListener()
    {
        if (_listenerHooked)
        {
            return;
        }

        _listenerHooked = true;
        try
        {
            _uiSettings.Value.ColorValuesChanged += (_, _) =>
                _dispatcherQueue?.TryEnqueue(ReapplyForSystemChange);
        }
        catch
        {
            // 订阅失败只影响「跟随系统」的实时切换，不影响本次主题应用
        }
    }

    /// <summary>系统主题变化回调（后台线程触发，已切回 UI 线程）：按最新系统状态重新套用。</summary>
    private static void ReapplyForSystemChange()
    {
        if (_root is null)
        {
            return;
        }

        ApplyMode(_window, _root, SettingsService.Instance.Settings);
    }

    /// <summary>按亚克力开关切换窗口背景：开启用桌面亚克力，关闭用纯色。</summary>
    private static void ApplyBackdrop(Window? window, bool acrylicOn)
    {
        if (window is null)
        {
            return;
        }

        if (acrylicOn)
        {
            _backdrop ??= new DesktopAcrylicBackdrop();
            window.SystemBackdrop = _backdrop;
        }
        else
        {
            window.SystemBackdrop = null;
        }
    }

    /// <summary>
    /// 按基底主题切换标题栏深浅色：通过 DWM 沉浸式深色模式属性实现，
    /// DWM 会自动处理标题文字与关闭/最小化/最大化按钮的前景与悬停态。
    /// 毛玻璃模式下标题栏区域本身透出桌面亚克力（SystemBackdrop 作用于整个窗口含标题栏），
    /// 无需再定制颜色，只需跟随基底深浅。
    /// </summary>
    private static void ApplyTitleBar(Window? window, bool isDark, bool acrylicOn)
    {
        if (window is null)
        {
            return;
        }

        try
        {
            var hwnd = WindowNative.GetWindowHandle(window);
            var dark = isDark ? 1 : 0;
            // 20 = DWMWA_USE_IMMERSIVE_DARK_MODE（Windows 10 19041+）
            _ = DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));
            // 34 = DWMWA_BORDER_COLOR：非客户区 1px 边框只有 DWM 能着色（XAML 内容覆盖不到），
            // 用当前主题的窗口底色让边框融入窗口边缘（深色配深、浅色配浅）。
            // 不能用 DWMWA_COLOR_NONE：它会让边框像素露出窗口白色底面，深色主题下反而出现白边。
            // COLORREF 格式 0x00BBGGRR：深色底 #1D1D20 → 0x00201D1D，浅色底 #F4F5F6 → 0x00F6F5F4
            var borderColor = isDark
                ? unchecked((int)0x00201D1D)
                : unchecked((int)0x00F6F5F4);
            _ = DwmSetWindowAttribute(hwnd, 34, ref borderColor, sizeof(int));
        }
        catch
        {
            // 标题栏定制失败只影响观感，不中断主题应用
        }
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(System.IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>
    /// 按当前主题更新卡片/状态栏表面笔刷（AppSurfaceBrush）：
    /// 毛玻璃开启时为对应深浅基底的亚克力磨砂笔刷（透出桌面、轻微磨砂），
    /// 关闭时为半透明纯色——卡片层次靠透明度与阴影区分，弱化硬边框。
    /// 弹窗单独使用透明度更高的 AppDialogBrush，保证文字对比度。
    /// 覆盖 App 资源字典同名键，所有引用该键的控件即时生效。
    /// </summary>
    private static void ApplySurfaceBrush(bool acrylicOn, bool isDark)
    {
        Brush brush;
        if (acrylicOn)
        {
            brush = new AcrylicBrush
            {
                // TintOpacity 0.45：更通透，磨砂下能明显透出桌面
                TintColor = isDark ? Color.FromArgb(255, 0x28, 0x28, 0x2C) : Color.FromArgb(255, 0xFF, 0xFF, 0xFF),
                TintOpacity = 0.45,
                FallbackColor = isDark
                    ? Color.FromArgb(255, 0x23, 0x23, 0x27)
                    : Color.FromArgb(255, 0xF7, 0xF8, 0xF9)
            };
        }
        else
        {
            // 不透明纯色：微信式深浅分层（浅=纯白卡叠浅灰底，深=比背景浅一级的深灰卡）
            brush = new SolidColorBrush(isDark
                ? Color.FromArgb(255, 0x28, 0x28, 0x2C)
                : Color.FromArgb(255, 0xFF, 0xFF, 0xFF));
        }

        Application.Current.Resources["AppSurfaceBrush"] = brush;

        // 弹窗表面不透明干净背景，圆角柔和（微信式）
        Application.Current.Resources["AppDialogBrush"] = new SolidColorBrush(isDark
            ? Color.FromArgb(255, 0x2C, 0x2C, 0x30)
            : Color.FromArgb(255, 0xF7, 0xF8, 0xF9));
    }

    /// <summary>
    /// 按主题模式更新内容区底色（AppContentBackgroundBrush，MainWindow 的 Frame 背景）：
    /// 纯色主题与页面背景一致；毛玻璃模式覆盖为透明，整个窗口透出桌面亚克力——
    /// 卡片/表格自带底色，文字对比度不受影响。
    /// </summary>
    private static void ApplyContentBackground(bool acrylicOn, bool isDark)
    {
        Application.Current.Resources["AppContentBackgroundBrush"] = acrylicOn
            ? new SolidColorBrush(Colors.Transparent)
            : new SolidColorBrush(isDark
                ? Color.FromArgb(255, 0x1D, 0x1D, 0x20)
                : Color.FromArgb(255, 0xF4, 0xF5, 0xF6));
    }

    /// <summary>
    /// 把十六进制强调色（如 #2563EB）应用到全局，覆盖 SystemAccentColor 及其亮/暗变体。
    /// 所有使用系统强调色的控件（AccentButtonStyle、CheckBox、Slider、ProgressBar 等）会即时变色。
    /// 空字符串或非法值不做处理，保留系统默认强调色。
    /// </summary>
    /// <param name="hexColor">十六进制颜色，支持 #RGB / #RRGGBB / #AARRGGBB。</param>
    public static void ApplyAccentColor(string? hexColor)
    {
        if (string.IsNullOrWhiteSpace(hexColor))
        {
            return;
        }

        if (!TryParseHex(hexColor, out var baseColor))
        {
            return;
        }

        var resources = Application.Current.Resources;
        resources["SystemAccentColor"] = baseColor;

        // 按 WinUI 3 系统强调色变体语义生成亮/暗三档，保证控件不同状态下层次清晰
        var light1 = ShiftLightness(baseColor, 0.10);
        var light2 = ShiftLightness(baseColor, 0.22);
        var light3 = ShiftLightness(baseColor, 0.38);
        var dark1 = ShiftLightness(baseColor, -0.10);
        var dark2 = ShiftLightness(baseColor, -0.22);
        var dark3 = ShiftLightness(baseColor, -0.38);

        resources["SystemAccentColorLight1"] = light1;
        resources["SystemAccentColorLight2"] = light2;
        resources["SystemAccentColorLight3"] = light3;
        resources["SystemAccentColorDark1"] = dark1;
        resources["SystemAccentColorDark2"] = dark2;
        resources["SystemAccentColorDark3"] = dark3;

        // WinUI 3 不内置 SystemAccentColor*Brush（UWP 才有），DesignTokens.xaml 中已定义默认值；
        // 这里同步覆盖为自定义强调色，使 DashboardPage 等引用该 Brush 的页面即时变色
        resources["SystemAccentColorDefaultBrush"] = new SolidColorBrush(baseColor);
        resources["SystemAccentColorLight1Brush"] = new SolidColorBrush(light1);
        resources["SystemAccentColorLight2Brush"] = new SolidColorBrush(light2);
        resources["SystemAccentColorLight3Brush"] = new SolidColorBrush(light3);
        resources["SystemAccentColorDark1Brush"] = new SolidColorBrush(dark1);
        resources["SystemAccentColorDark2Brush"] = new SolidColorBrush(dark2);
        resources["SystemAccentColorDark3Brush"] = new SolidColorBrush(dark3);
    }

    /// <summary>解析十六进制颜色字符串。</summary>
    private static bool TryParseHex(string hex, out Color color)
    {
        color = default;
        var s = hex.TrimStart('#');
        if (s.Length == 3)
        {
            s = new string(new[] { s[0], s[0], s[1], s[1], s[2], s[2] });
        }
        if (s.Length != 6 && s.Length != 8)
        {
            return false;
        }

        try
        {
            byte a = 255;
            int offset = 0;
            if (s.Length == 8)
            {
                a = Convert.ToByte(s[..2], 16);
                offset = 2;
            }
            byte r = Convert.ToByte(s.Substring(offset, 2), 16);
            byte g = Convert.ToByte(s.Substring(offset + 2, 2), 16);
            byte b = Convert.ToByte(s.Substring(offset + 4, 2), 16);
            color = Color.FromArgb(a, r, g, b);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>将颜色在 HSL 空间内调整亮度，返回新颜色（保留 Alpha）。</summary>
    private static Color ShiftLightness(Color c, double delta)
    {
        double r = c.R / 255.0;
        double g = c.G / 255.0;
        double b = c.B / 255.0;

        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double l = (max + min) / 2.0;

        if (Math.Abs(max - min) < 1e-6)
        {
            // 灰度：仅调整亮度
            double v = Math.Clamp(l + delta, 0, 1);
            byte iv = (byte)Math.Round(v * 255);
            return Color.FromArgb(c.A, iv, iv, iv);
        }

        double s = l > 0.5 ? (max - min) / (2.0 - max - min) : (max - min) / (max + min);
        double newL = Math.Clamp(l + delta, 0, 1);

        // HSL -> RGB
        double q = newL < 0.5 ? newL * (1 + s) : newL + s - newL * s;
        double p = 2 * newL - q;

        double h;
        if (max == r)
        {
            h = (g - b) / (max - min) + (g < b ? 6 : 0);
        }
        else if (max == g)
        {
            h = (b - r) / (max - min) + 2;
        }
        else
        {
            h = (r - g) / (max - min) + 4;
        }
        h /= 6.0;

        double Hr = HueToRgb(p, q, h + 1.0 / 3.0);
        double Hg = HueToRgb(p, q, h);
        double Hb = HueToRgb(p, q, h - 1.0 / 3.0);

        return Color.FromArgb(
            c.A,
            (byte)Math.Round(Hr * 255),
            (byte)Math.Round(Hg * 255),
            (byte)Math.Round(Hb * 255));
    }

    private static double HueToRgb(double p, double q, double t)
    {
        if (t < 0) t += 1;
        if (t > 1) t -= 1;
        if (t < 1.0 / 6.0) return p + (q - p) * 6 * t;
        if (t < 1.0 / 2.0) return q;
        if (t < 2.0 / 3.0) return p + (q - p) * (2.0 / 3.0 - t) * 6;
        return p;
    }
}
