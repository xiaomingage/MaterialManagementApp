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

        /// <summary>
        /// 当前应用的主窗口实例。文件选择器（FileOpenPicker）等 Win32 对话框 API
        /// 需要窗口句柄，页面通过该属性获取。
        /// </summary>
        public static Window? MainWindow { get; private set; }

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

            // 正常退出时冲刷缓冲，确保最后的日志落盘
            _window.Closed += (_, _) =>
            {
                _logger.LogInformation("应用正常退出，版本=v{Version}。", typeof(App).Assembly.GetName().Version);
                Serilog.Log.CloseAndFlush();
            };
        }
    }
}
