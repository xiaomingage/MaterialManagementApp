using System;
using System.Runtime.InteropServices;

namespace MaterialManagementApp.Common;

/// <summary>
/// 系统托盘图标管理器（单例）。基于 Win32 Shell_NotifyIcon 实现：
/// 内部创建一个消息专用隐藏窗口承载托盘回调，左键单击唤起主窗口，
/// 右键弹出「显示主窗口 / 退出软件」菜单。只负责图标与菜单编排，
/// 窗口显隐与退出动作通过回调交由 MainWindow 完成，不涉及业务逻辑。
/// </summary>
public sealed class TrayIconManager
{
    public static TrayIconManager Instance { get; } = new();

    private const uint WmAppTrayCallback = 0x8001;   // WM_APP + 1，托盘回调消息
    private const uint WmDestroy = 0x0002;
    private const int NimAdd = 0x0;
    private const int NimDelete = 0x2;
    private const uint NifMessage = 0x1;
    private const uint NifIcon = 0x2;
    private const uint NifTip = 0x4;
    private const long WmLbuttonUp = 0x0202;
    private const long WmLbuttonDblClk = 0x0203;
    private const long WmRbuttonUp = 0x0205;
    private const uint ImageIcon = 1;
    private const uint LoadFromFile = 0x10;
    private const uint LoadDefaultSize = 0x40;
    private const int SwRestore = 9;
    private const uint MfString = 0x0;
    private const uint MfSeparator = 0x800;
    private const uint TpmRightButton = 0x2;
    private const uint TpmReturnCmd = 0x100;
    private const int MenuShow = 1;
    private const int MenuExit = 2;
    private const string HostClassName = "MaterialManagementApp_TrayHostWnd";

    /// <summary>窗口过程委托：静态实例常驻，防止封送后被 GC 回收导致回调崩溃。</summary>
    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private static readonly WndProcDelegate WndProc = HostWndProc;

    private static readonly IntPtr HwndMessage = new(-3);

    private readonly object _sync = new();

    private IntPtr _hostWindow;
    private IntPtr _iconHandle;
    private bool _iconVisible;
    private bool _classRegistered;

    private string _iconPath = string.Empty;
    private string _tooltip = string.Empty;
    private Action? _onShowRequested;
    private Action? _onExitRequested;

    private TrayIconManager()
    {
    }

    /// <summary>托盘图标当前是否已显示。</summary>
    public bool IsIconVisible
    {
        get { lock (_sync) { return _iconVisible; } }
    }

    /// <summary>
    /// 配置托盘：图标文件、悬停提示与左键唤起/退出回调。启动时调用一次。
    /// </summary>
    public void Configure(
        string iconPath,
        string tooltip,
        Action onShowRequested,
        Action onExitRequested)
    {
        lock (_sync)
        {
            _iconPath = iconPath;
            _tooltip = tooltip ?? string.Empty;
            _onShowRequested = onShowRequested;
            _onExitRequested = onExitRequested;
        }
    }

    /// <summary>开启或关闭托盘图标；重复调用幂等。</summary>
    public void SetEnabled(bool enabled)
    {
        lock (_sync)
        {
            if (enabled)
            {
                EnsureIcon();
            }
            else
            {
                DestroyIconAndHost();
            }
        }
    }

    /// <summary>进程退出前的最终清理（幂等）。</summary>
    public void Shutdown()
    {
        lock (_sync)
        {
            DestroyIconAndHost();
        }
    }

    /// <summary>显示并激活指定窗口（还原最小化状态），供托盘左键唤起主窗口使用。</summary>
    public static void ShowAndActivate(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        ShowWindow(hwnd, SwRestore);
        SetForegroundWindow(hwnd);
    }

    // ==================== 内部实现 ====================

    private void EnsureIcon()
    {
        if (_iconVisible)
        {
            return;
        }

        if (_hostWindow == IntPtr.Zero)
        {
            CreateHostWindow();
        }

        if (_iconHandle == IntPtr.Zero)
        {
            _iconHandle = LoadImage(IntPtr.Zero, _iconPath, ImageIcon, 0, 0, LoadDefaultSize | LoadFromFile);
            if (_iconHandle == IntPtr.Zero)
            {
                System.Diagnostics.Debug.WriteLine("[TrayIcon] 加载图标文件失败。");
                return;
            }
        }

        var data = CreateNotifyIconData();
        if (Shell_NotifyIcon(NimAdd, ref data))
        {
            _iconVisible = true;
        }
        else
        {
            System.Diagnostics.Debug.WriteLine("[TrayIcon] 注册托盘图标失败。");
        }
    }

    private void DestroyIconAndHost()
    {
        if (_iconVisible)
        {
            var data = CreateNotifyIconData();
            Shell_NotifyIcon(NimDelete, ref data);
            _iconVisible = false;
        }

        if (_iconHandle != IntPtr.Zero)
        {
            DestroyIcon(_iconHandle);
            _iconHandle = IntPtr.Zero;
        }

        if (_hostWindow != IntPtr.Zero)
        {
            DestroyWindow(_hostWindow);
            _hostWindow = IntPtr.Zero;
        }
    }

    private NOTIFYICONDATA CreateNotifyIconData()
    {
        return new NOTIFYICONDATA
        {
            cbSize = (int)Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hostWindow,
            uID = 1,
            uFlags = NifMessage | NifIcon | NifTip,
            uCallbackMessage = WmAppTrayCallback,
            hIcon = _iconHandle,
            szTip = _tooltip,
            szInfo = string.Empty,
            szInfoTitle = string.Empty
        };
    }

    private void CreateHostWindow()
    {
        if (!_classRegistered)
        {
            var wndClass = new WNDCLASS
            {
                lpfnWndProc = WndProc,
                lpszClassName = HostClassName,
                hInstance = GetModuleHandle(null),
            };
            _classRegistered = RegisterClass(ref wndClass) != 0;
        }

        // HWND_MESSAGE：消息专用窗口，不参与 UI，仅接收托盘回调
        _hostWindow = CreateWindowEx(
            0, HostClassName, string.Empty, 0,
            0, 0, 0, 0,
            HwndMessage, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
    }

    private static IntPtr HostWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WmAppTrayCallback)
        {
            Instance.HandleTrayMouse(lParam);
        }
        else if (msg == WmDestroy)
        {
            // 消息窗口销毁时兜底移除托盘图标，防止任务栏托盘残留“僵尸图标”
            if (Instance._iconVisible)
            {
                var data = Instance.CreateNotifyIconData();
                Shell_NotifyIcon(NimDelete, ref data);
                Instance._iconVisible = false;
            }
        }

        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private void HandleTrayMouse(IntPtr lParam)
    {
        var message = lParam.ToInt64() & 0xFFFF;
        switch (message)
        {
            case WmLbuttonUp:
            case WmLbuttonDblClk:
                _onShowRequested?.Invoke();
                break;
            case WmRbuttonUp:
                ShowContextMenu();
                break;
        }
    }

    private void ShowContextMenu()
    {
        if (_hostWindow == IntPtr.Zero)
        {
            return;
        }

        var menu = CreatePopupMenu();
        AppendMenu(menu, MfString, (IntPtr)MenuShow, "显示主窗口");
        AppendMenu(menu, MfSeparator, IntPtr.Zero, null);
        AppendMenu(menu, MfString, (IntPtr)MenuExit, "退出软件");

        GetCursorPos(out var point);

        // 先把前台焦点切到托盘消息窗口，否则点击菜单外部时菜单不会自动消失
        SetForegroundWindow(_hostWindow);
        var command = TrackPopupMenuEx(
            menu, TpmRightButton | TpmReturnCmd,
            point.X, point.Y, _hostWindow, IntPtr.Zero);
        DestroyMenu(menu);

        if (command == MenuShow)
        {
            _onShowRequested?.Invoke();
        }
        else if (command == MenuExit)
        {
            _onExitRequested?.Invoke();
        }
    }

    // ==================== Win32 互操作 ====================

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;

        public int dwState;
        public int dwStateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;

        public uint uVersion;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;

        public int dwInfoFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WNDCLASS
    {
        public uint style;
        public WndProcDelegate lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string lpszMenuName;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string lpszClassName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Shell_NotifyIcon(int message, ref NOTIFYICONDATA data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadImage(IntPtr hInstance, string name, uint type, int cx, int cy, uint load);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClass(ref WNDCLASS wndClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr menu, uint flags, IntPtr idNewItem, string? newItem);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenuEx(
        IntPtr menu, uint flags, int x, int y, IntPtr hwnd, IntPtr tpmParams);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int cmdShow);
}
