using MaterialManagementApp.Common;
using MaterialManagementApp.Services;
using Microsoft.Extensions.Logging;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.Foundation;
using Windows.Foundation.Collections;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace MaterialManagementApp
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    public partial class App : Application
    {
        private static readonly ILogger _logger = AppLog.CreateLogger<App>();

        private Window? _window;

        /// <summary>单实例互斥体名称（Local\ 仅本会话，无需跨用户会话唯一）。</summary>
        private const string SingleInstanceMutexName = @"Local\MaterialManagementApp_SingleInstance_8F3C2A1E-5B6D-4E7A-9C1F-2D3A4B5C6D7E";

        /// <summary>第二次实例用于唤醒已有实例主窗口的事件名称。</summary>
        private const string ActivateEventName = @"Local\MaterialManagementApp_Activate_8F3C2A1E-5B6D-4E7A-9C1F-2D3A4B5C6D7E";

        private static Mutex? _singleInstanceMutex;
        private static EventWaitHandle? _activateEvent;
        private static CancellationTokenSource? _activateWatchCts;

        /// <summary>UI 线程调度器：OnLaunched（UI 线程）捕获，供后台唤醒监听线程回投主窗口操作。</summary>
        private static Microsoft.UI.Dispatching.DispatcherQueue? _uiDispatcherQueue;

        /// <summary>
        /// 当前应用的主窗口实例。文件选择器（FileOpenPicker）等 Win32 对话框 API
        /// 需要窗口句柄，页面通过该属性获取。
        /// </summary>
        public static Window? MainWindow { get; private set; }

        /// <summary>
        /// 单实例闸门：程序全生命周期只允许一个实例运行。
        /// 首个实例持有命名 Mutex 并监听唤醒事件；后续实例发现 Mutex 已被占用时，
        /// 通过命名事件通知已有实例唤起主窗口（含从托盘还原），然后立即退出自身，
        /// 保证「程序保持唯一性」且用户始终能看到正在运行的窗口。
        /// </summary>
        /// <returns>true 表示本实例是唯一实例并已开始监听；false 表示已有实例在运行。</returns>
        private bool AcquireSingleInstance()
        {
            _singleInstanceMutex = new Mutex(true, SingleInstanceMutexName, out var createdNew);
            if (createdNew)
            {
                _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
                _uiDispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
                _activateWatchCts = new CancellationTokenSource();
                _ = Task.Run(() => WatchActivateRequests(_activateWatchCts.Token));
                _logger.LogInformation("单实例闸门获取成功，本进程为唯一运行实例。");
                return true;
            }

            // 已有实例在运行：通知其唤起主窗口后退出当前实例
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
            try
            {
                using var existing = EventWaitHandle.OpenExisting(ActivateEventName);
                existing.Set();
                _logger.LogInformation("检测到程序已在运行，已通知已有实例唤起主窗口。");
            }
            catch (Exception ex)
            {
                _logger.LogWarning("通知已有实例唤起窗口失败：{Message}", ex.Message);
            }
            return false;
        }

        /// <summary>后台监听唤醒事件：收到信号即把主窗口显示并前置（托盘隐藏状态同样适用）。</summary>
        private void WatchActivateRequests(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                bool signaled;
                try
                {
                    signaled = _activateEvent!.WaitOne();
                }
                catch
                {
                    // 事件句柄已随应用退出被释放（ObjectDisposedException 等），结束监听线程
                    return;
                }

                if (!signaled)
                {
                    return;
                }

                try
                {
                    _uiDispatcherQueue?.TryEnqueue(() =>
                    {
                        if (_window is MainWindow main)
                        {
                            main.ShowFromTray();
                        }
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("唤起主窗口失败：{Message}", ex.Message);
                }
            }
        }

        /// <summary>
        /// 应用退出时释放单实例资源：停止唤醒监听、释放事件句柄与互斥体，
        /// 保证进程结束后不留任何与程序相关的内核对象或驻留线程。
        /// </summary>
        private void ShutdownSingleInstanceGuard()
        {
            try
            {
                _activateWatchCts?.Cancel();
            }
            catch { }

            try
            {
                _activateEvent?.Dispose();
            }
            catch { }

            try
            {
                _activateWatchCts?.Dispose();
            }
            catch { }

            try
            {
                if (_singleInstanceMutex is not null)
                {
                    _singleInstanceMutex.ReleaseMutex();
                    _singleInstanceMutex.Dispose();
                }
            }
            catch
            {
                // 互斥体释放失败（如线程所有权不符）不影响退出，进程结束后内核自动回收
            }

            _activateEvent = null;
            _activateWatchCts = null;
            _singleInstanceMutex = null;
        }

        /// <summary>
        /// Initializes the singleton application object.  This is the first line of authored code
        /// executed, and as such is the logical equivalent of main() or WinMain().
        /// </summary>
        public App()
        {
            InitializeComponent();

            // 全局异常兜底：任何一层漏接的异常都会写入 Logs\system-*.log 后再按原行为终止
            UnhandledException += OnAppUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        }

        /// <summary>UI 线程未处理异常（WinUI 层）。记录后保持默认行为（应用终止）。</summary>
        private void OnAppUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
        {
            _logger.LogCritical(e.Exception, "发生未处理的 UI 线程异常，应用即将退出。");
            Serilog.Log.CloseAndFlush();
        }

        /// <summary>非 UI 线程未处理异常（运行时层兜底）。</summary>
        private void OnDomainUnhandledException(object sender, System.UnhandledExceptionEventArgs e)
        {
            _logger.LogCritical(e.ExceptionObject as Exception, "发生未处理的应用域异常。IsTerminating={IsTerminating}", e.IsTerminating);
            Serilog.Log.CloseAndFlush();
        }

        /// <summary>Task 中被吞掉的异常（未 await / 未观察）。</summary>
        private void OnUnobservedTaskException(object? sender, System.Threading.Tasks.UnobservedTaskExceptionEventArgs e)
        {
            _logger.LogError(e.Exception, "检测到未被观察的 Task 异常。");
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

        /// <summary>
        /// 弹出致命错误对话框（Win32 MessageBox，不依赖任何 XAML 窗口），
        /// 用户确认后由调用方退出应用。
        /// </summary>
        private static void ShowFatalDialog(string message)
        {
            MessageBoxW(IntPtr.Zero, message, "物资管理系统 - 数据库错误", 0x10 /* MB_ICONERROR | MB_OK */);
        }

        /// <summary>
        /// Invoked when the application is launched.
        /// </summary>
        /// <param name="args">Details about the launch request and process.</param>
        protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            // 单实例闸门：已有实例在运行时，通知其唤起主窗口后直接退出本实例
            if (!AcquireSingleInstance())
            {
                Exit();
                return;
            }

            // 生命周期日志带版本号：线上排障时可直接对齐日志与安装包版本
            _logger.LogInformation("应用启动中，版本=v{Version}。", typeof(App).Assembly.GetName().Version);

            // 数据库版本迁移闸门：旧库搬迁、版本校验、迁移前自动备份、增量迁移。
            // 失败时弹窗拦截，禁止进入主界面，防止迁移不完整的库被业务代码继续写入造成数据损坏。
            var migration = DatabaseMigrator.PrepareAndMigrate();
            if (!migration.Success)
            {
                _logger.LogCritical("数据库迁移未通过，应用拒绝进入主界面。{Message}", migration.Message);
                ShowFatalDialog(migration.Message);
                Serilog.Log.CloseAndFlush();
                Exit();
                return;
            }

            // 数据库初始化失败不应阻塞界面启动：记录诊断信息后继续，避免启动即白屏或闪退
            try
            {
                DatabaseService.Instance.Initialize();
                _logger.LogInformation("数据库初始化完成。");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "数据库初始化失败。");
            }

            _window = new MainWindow();
            MainWindow = _window;

            // 启动时应用已保存的主题模式（含毛玻璃开关与降级）；SettingsService 内部已容错，配置损坏时回退默认值
            ThemeHelper.ApplyMode(_window, _window.Content as FrameworkElement, SettingsService.Instance.Settings);
            ThemeHelper.ApplyAccentColor(SettingsService.Instance.Settings.AccentColor);

            _window.Activate();

            // 正常退出时释放单实例资源并冲刷缓冲，确保最后的日志落盘、不遗留任何进程相关对象
            _window.Closed += (_, _) =>
            {
                ShutdownSingleInstanceGuard();
                _logger.LogInformation("应用正常退出，版本=v{Version}。", typeof(App).Assembly.GetName().Version);
                Serilog.Log.CloseAndFlush();
            };
        }
    }
}
