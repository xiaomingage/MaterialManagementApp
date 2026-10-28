using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using MaterialManagementApp.Models;
using MaterialManagementApp.Services;
using MaterialManagementApp.Services.Export;
using MaterialManagementApp.ViewModels;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.UI.Popups;

namespace MaterialManagementApp.Pages;

/// <summary>
/// 供应商合同页面（供应商管理 → 供应商合同）。
/// 上半部分维护供应商合同信息（名称、合同编号、联系人、签订日期），
/// 下半部分管理该供应商的合同 PDF 文件：上传后存到数据目录 Contracts/ 下，可一键用系统默认 PDF 阅读器打开。
/// </summary>
public sealed partial class SupplierContractPage : Page
{
    /// <summary>页面视图模型，供 XAML 通过 x:Bind 绑定。</summary>
    public SupplierContractViewModel ViewModel { get; } = new();

    /// <summary>初始化页面。</summary>
    public SupplierContractPage()
    {
        InitializeComponent();
    }

    /// <summary>进入页面时按当前筛选条件加载供应商合同。</summary>
    /// <param name="e">导航事件参数。</param>
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel.LoadCommand.Execute(null);
    }

    /// <summary>表格选中行变化时同步到视图模型，并刷新合同文件区。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">选中项变化参数。</param>
    private void OnLedgerSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ViewModel.SelectedSupplier = LedgerGrid.SelectedItem as Supplier;
        RefreshContractFilePanel();
    }

    /// <summary>根据当前选中供应商刷新合同文件区的文件名与按钮状态。</summary>
    private void RefreshContractFilePanel()
    {
        var supplier = ViewModel.SelectedSupplier;
        if (supplier is null)
        {
            ContractFileNameText.Text = "未选择供应商";
            OpenContractBtn.IsEnabled = false;
            ClearContractBtn.IsEnabled = false;
            ContractFileStatusText.Text = string.Empty;
            return;
        }

        var hasFile = !string.IsNullOrWhiteSpace(supplier.ContractFilePath);
        ContractFileNameText.Text = hasFile ? Path.GetFileName(supplier.ContractFilePath) : "（未上传合同文件）";
        OpenContractBtn.IsEnabled = hasFile;
        ClearContractBtn.IsEnabled = hasFile;
        ContractFileStatusText.Text = string.Empty;
    }

    /// <summary>
    /// 「新增」：在表格首行插入空白行，并让该行立即进入编辑状态，
    /// 用户无需弹窗即可直接开始录入。
    /// </summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">路由事件参数。</param>
    private void OnNewRowClick(object sender, RoutedEventArgs e)
    {
        ViewModel.NewRowCommand.Execute(null);

        var row = ViewModel.Items.FirstOrDefault(x => x.IsNewRow);
        if (row is null)
        {
            return;
        }

        LedgerGrid.SelectedItem = row;
        LedgerGrid.CurrentColumn = LedgerGrid.Columns[0];
        LedgerGrid.ScrollIntoView(row, LedgerGrid.Columns[0]);

        // 新行的单元格容器要等本次布局完成后才生成，BeginEdit 必须延后一帧再调用
        DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                LedgerGrid.BeginEdit();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SupplierContractPage] 自动进入编辑失败：{ex.Message}");
            }
        });
    }

    /// <summary>行内「保存」：新增行执行入库，已有行执行更新。</summary>
    /// <param name="sender">被点击的按钮。</param>
    /// <param name="e">路由事件参数。</param>
    private void OnSaveRowClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: Supplier row })
        {
            ViewModel.SaveRow(row);
            RefreshContractFilePanel();
        }
    }

    /// <summary>行内「取消」：移除尚未入库的空白新增行。</summary>
    /// <param name="sender">被点击的按钮。</param>
    /// <param name="e">路由事件参数。</param>
    private void OnCancelNewRowClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: Supplier row })
        {
            ViewModel.CancelNewRow(row);
        }
    }

    /// <summary>行内「删除」：二次确认后从集合与数据库中移除该行。</summary>
    /// <param name="sender">被点击的按钮。</param>
    /// <param name="e">路由事件参数。</param>
    private async void OnDeleteRowClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: Supplier row })
        {
            return;
        }

        await ConfirmAndDeleteAsync(row);
    }

    /// <summary>工具栏「删除」：二次确认后删除当前选中行；未选中时提示。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">路由事件参数。</param>
    private async void OnDeleteSelectedClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedSupplier is not { } row)
        {
            ViewModel.StatusMessage = "请先在表格中选中要删除的行。";
            return;
        }

        await ConfirmAndDeleteAsync(row);
    }

    /// <summary>导出：把当前列表（不含未保存的空白行）写入 Excel 并弹出保存对话框。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">路由事件参数。</param>
    private async void OnExportClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var rows = ViewModel.GetFilteredRowsForExport()
                .Where(x => !x.IsNewRow)
                .Select(x => (IReadOnlyList<string?>)new string?[]
                {
                    x.Name,
                    x.ContractNo,
                    x.ContactPerson,
                    x.SignDate,
                    x.ContractFilePath
                })
                .ToList();

            string[] headers = { "供应商名称", "合同编号", "联系人", "签订日期", "合同文件" };
            var saved = await SimpleListExport.ExportAsync("供应商合同", headers, rows);
            ViewModel.StatusMessage = saved ? "已导出供应商合同。" : "已取消导出。";
        }
        catch (Exception ex)
        {
            ViewModel.StatusMessage = $"导出失败：{ex.Message}";
        }
    }

    /// <summary>弹出确认对话框，确认后删除指定行。</summary>
    /// <param name="row">待删除的记录。</param>
    private async System.Threading.Tasks.Task ConfirmAndDeleteAsync(Supplier row)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "确认删除",
            Content = $"确定删除供应商「{row.Name}」的合同（{row.ContractNo}）吗？该操作不可撤销。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            ViewModel.DeleteRow(row);
            RefreshContractFilePanel();
        }
    }

    // ------------------------------------------------------------------
    // 合同 PDF 文件管理
    // ------------------------------------------------------------------

    /// <summary>合同 PDF 在数据目录下的相对子目录名。</summary>
    private const string ContractsSubDir = "Contracts";

    /// <summary>解析数据根目录：安装版取 AppPaths.DataRoot，开发态退回程序目录下的 Data。</summary>
    /// <returns>数据目录绝对路径。</returns>
    private static string ResolveDataRoot()
    {
        return AppPaths.DataRoot ?? Path.Combine(AppContext.BaseDirectory, "Data");
    }

    /// <summary>把供应商的合同文件相对路径解析为绝对路径。</summary>
    /// <param name="supplier">供应商实体。</param>
    /// <returns>合同文件绝对路径；未上传时返回空串。</returns>
    private static string ResolveContractAbsolutePath(Supplier supplier)
    {
        if (string.IsNullOrWhiteSpace(supplier.ContractFilePath))
        {
            return string.Empty;
        }

        return Path.IsPathRooted(supplier.ContractFilePath)
            ? supplier.ContractFilePath
            : Path.Combine(ResolveDataRoot(), supplier.ContractFilePath);
    }

    /// <summary>
    /// 弹出 WinUI 原生 MessageDialog 错误弹窗（模态、阻塞交互）。
    /// MessageDialog 在 WinUI 3 非打包应用中必须先设置窗口句柄，否则 ShowAsync 会抛异常。
    /// </summary>
    /// <param name="title">弹窗标题。</param>
    /// <param name="content">弹窗正文，不得包含原始路径等内部信息。</param>
    /// <returns>用户关闭弹窗后完成。</returns>
    private static async System.Threading.Tasks.Task ShowErrorDialogAsync(string title, string content)
    {
        var dialog = new MessageDialog(content, title)
        {
            Commands = { new UICommand("确认") },
            DefaultCommandIndex = 0,
            CancelCommandIndex = 0
        };

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow!);
        WinRT.Interop.InitializeWithWindow.Initialize(dialog, hwnd);
        await dialog.ShowAsync();
    }

    /// <summary>
    /// 上传合同 PDF：弹出文件选择器，复制到数据目录 Contracts/ 下，
    /// 相对路径写回 Supplier.ContractFilePath 并落库。
    /// 任何失败都以模态 MessageDialog 提示用户，不把原始路径/异常细节暴露到界面；
    /// 详细信息（时间、供应商、合同编号、目标路径、异常）写入本地日志便于排查。
    /// </summary>
    private async void OnUploadContractClick(object sender, RoutedEventArgs e)
    {
        var supplier = ViewModel.SelectedSupplier;
        if (supplier is null)
        {
            ViewModel.StatusMessage = "请先在表格中选中一个供应商，再上传合同文件。";
            return;
        }

        if (supplier.IsNewRow || supplier.Id == 0)
        {
            ViewModel.StatusMessage = "请先保存该供应商，再上传合同文件。";
            return;
        }

        var logger = AppLog.CreateLogger<SupplierContractPage>();
        StorageFile? file = null;

        try
        {
            var picker = new FileOpenPicker
            {
                ViewMode = PickerViewMode.List,
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary
            };
            picker.FileTypeFilter.Add(".pdf");

            // WinUI 3 必须设置窗口句柄，否则 PickSingleFileAsync 会抛异常
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow!);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            file = await picker.PickSingleFileAsync();
            if (file is null)
            {
                ContractFileStatusText.Text = "已取消选择。";
                return;
            }

            var contractsDir = Path.Combine(ResolveDataRoot(), ContractsSubDir);

            // 目标项目存储文件夹不存在（且创建失败）时：弹窗提示、不上传、不写库，表单内容保持不变
            if (!Directory.Exists(contractsDir))
            {
                try
                {
                    Directory.CreateDirectory(contractsDir);
                }
                catch (Exception dirEx)
                {
                    logger.LogError(dirEx,
                        "创建合同存储目录失败。时间={Time:yyyy-MM-dd HH:mm:ss}，供应商={Supplier}，合同编号={ContractNo}，目标路径={Dir}。",
                        DateTime.Now, supplier.Name, supplier.ContractNo, contractsDir);
                    await ShowErrorDialogAsync("保存失败", "保存失败，在指定的路径找不到该项目目录，请先创建项目文件夹！");
                    ContractFileStatusText.Text = string.Empty;
                    return;
                }
            }

            // 文件名带上供应商 Id，避免重名覆盖；保留原始扩展名
            var safeName = string.IsNullOrWhiteSpace(supplier.Name)
                ? $"supplier-{supplier.Id}"
                : supplier.Name;
            var destFileName = $"{supplier.Id}_{safeName}{Path.GetExtension(file.Name)}";
            var destPath = Path.Combine(contractsDir, destFileName);

            // 通过文件夹 API 复制（GetFileFromPathAsync 在目标文件不存在时会直接抛异常，不能用来做覆盖）
            var folder = await StorageFolder.GetFolderFromPathAsync(contractsDir);
            await file.CopyAsync(folder, destFileName, NameCollisionOption.ReplaceExisting);

            // 存储相对路径，便于随数据目录一起迁移
            var relative = Path.Combine(ContractsSubDir, destFileName);
            supplier.ContractFilePath = relative;
            DatabaseService.Instance.UpdateSupplier(supplier);

            RefreshContractFilePanel();
            ViewModel.StatusMessage = $"合同文件已上传：{destFileName}";
            ContractFileStatusText.Text = $"已保存到数据目录，相对路径：{relative}";
            logger.LogInformation(
                "上传合同文件成功。时间={Time:yyyy-MM-dd HH:mm:ss}，供应商 Id={Id}，供应商={Supplier}，合同编号={ContractNo}，文件={File}。",
                DateTime.Now, supplier.Id, supplier.Name, supplier.ContractNo, destFileName);
        }
        catch (Exception ex)
        {
            // 模态弹窗只给友好文案，原始异常与路径只进日志，不暴露给用户
            logger.LogError(ex,
                "上传合同文件失败。时间={Time:yyyy-MM-dd HH:mm:ss}，供应商 Id={Id}，供应商={Supplier}，合同编号={ContractNo}，源文件={Source}。",
                DateTime.Now, supplier.Id, supplier.Name, supplier.ContractNo, file?.Path ?? "(未选择)");
            await ShowErrorDialogAsync("上传失败", "上传合同文件失败，请确认文件未被占用后重试。详情请查看日志。");
            ContractFileStatusText.Text = string.Empty;
        }
    }

    /// <summary>打开当前供应商的合同 PDF（用系统默认 PDF 阅读器）。</summary>
    private void OnOpenContractClick(object sender, RoutedEventArgs e)
    {
        var supplier = ViewModel.SelectedSupplier;
        if (supplier is null)
        {
            return;
        }

        var path = ResolveContractAbsolutePath(supplier);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            ContractFileStatusText.Text = "合同文件不存在，可能已被移动或删除。";
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            ContractFileStatusText.Text = "已打开合同文件。";
        }
        catch (Exception ex)
        {
            AppLog.CreateLogger<SupplierContractPage>().LogError(ex, "打开合同文件失败，路径={Path}。", path);
            ContractFileStatusText.Text = $"打开失败：{ex.Message}";
        }
    }

    /// <summary>清除当前供应商的合同文件关联（不删除磁盘文件，避免误删）。</summary>
    private async void OnClearContractClick(object sender, RoutedEventArgs e)
    {
        var supplier = ViewModel.SelectedSupplier;
        if (supplier is null || string.IsNullOrWhiteSpace(supplier.ContractFilePath))
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "清除合同文件",
            Content = $"确定解除「{supplier.Name}」与合同文件的关联吗？（磁盘文件不会被删除）",
            PrimaryButtonText = "清除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        supplier.ContractFilePath = string.Empty;
        DatabaseService.Instance.UpdateSupplier(supplier);
        RefreshContractFilePanel();
        ViewModel.StatusMessage = "已清除合同文件关联。";
    }
}
