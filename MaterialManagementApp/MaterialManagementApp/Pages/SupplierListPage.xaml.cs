using System;
using System.Collections.Generic;
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
using Windows.Storage.Pickers;

namespace MaterialManagementApp.Pages;

/// <summary>
/// 供应商列表页面（供应商管理 → 供应商列表）。
/// 上半部分为查询筛选面板，下半部分为可编辑表格，
/// 「新增」在表格首行插入一条全编辑状态的空白行，行末「保存」校验后入库、「取消」直接移除。
/// </summary>
public sealed partial class SupplierListPage : Page
{
    /// <summary>页面视图模型，供 XAML 通过 x:Bind 绑定。</summary>
    public SupplierListViewModel ViewModel { get; } = new();

    /// <summary>合同材料单价明细视图模型，供 XAML 通过 x:Bind 绑定。</summary>
    public ContractItemViewModel ContractItems { get; } = new();

    /// <summary>初始化页面。</summary>
    public SupplierListPage()
    {
        InitializeComponent();
        ContractItems.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(ContractItemViewModel.StatusMessage))
            {
                ItemStatusText.Text = ContractItems.StatusMessage;
            }
        };

        // 空白行防呆联动：新增草稿行被表格自动移除时，同步解除 VM 的草稿标记并提示
        LedgerGrid.BlankRowRemoved += (s, row) =>
        {
            ViewModel.StatusMessage = "检测到空白行，已自动移除。";
        };
        ItemGrid.BlankRowRemoved += (s, row) =>
        {
            ContractItems.DiscardDraft(row as SupplierContractItem);
            ContractItems.StatusMessage = "检测到空白行，已自动移除。";
        };
    }

    /// <summary>进入页面时按当前筛选条件加载供应商列表。</summary>
    /// <param name="e">导航事件参数。</param>
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel.LoadCommand.Execute(null);
    }

    /// <summary>表格选中行变化时同步到视图模型，并联动加载该供应商的合同材料单价明细。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">选中项变化参数。</param>
    private void OnLedgerSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ViewModel.SelectedSupplier = LedgerGrid.SelectedItem as Supplier;

        // 同一供应商重复触发选中变化时不重载，避免打断正在进行的明细编辑
        if (ViewModel.SelectedSupplier?.Id == ContractItems.CurrentSupplierId)
        {
            return;
        }

        // 关键防崩溃：先取消明细表尚未提交的单元格编辑，再整体替换 Items 数据源。
        // CommunityToolkit DataGrid 在编辑事务未关闭时被外部改动集合会直接抛异常闪退
        ItemGrid.CancelPendingEdit();
        ContractItems.LoadForSupplier(ViewModel.SelectedSupplier?.Id);
    }

    /// <summary>
    /// 「新增」：在表格首行插入空白行，并让该行立即进入编辑状态，
    /// 用户无需弹窗即可直接开始录入。
    /// </summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">路由事件参数。</param>
    private void OnNewRowClick(object sender, RoutedEventArgs e)
    {
        // 防崩溃：插入新行前先结束上一行的编辑事务，避免编辑态下改动集合
        LedgerGrid.CancelPendingEdit();
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
        LedgerGrid.BeginEditWhenReady();
    }

    /// <summary>行内「保存」：新增行执行入库，已有行执行更新。</summary>
    /// <param name="sender">被点击的按钮。</param>
    /// <param name="e">路由事件参数。</param>
    private void OnSaveRowClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: Supplier row })
        {
            LedgerGrid.CancelPendingEdit();
            ViewModel.SaveRow(row);
        }
    }

    /// <summary>行内「取消」：移除尚未入库的空白新增行。</summary>
    /// <param name="sender">被点击的按钮。</param>
    /// <param name="e">路由事件参数。</param>
    private void OnCancelNewRowClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: Supplier row })
        {
            LedgerGrid.CancelPendingEdit();
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
            // 导出全部筛选结果而非当前页；尚未保存的空白行不入导出结果，避免把空记录写进 Excel
            var rows = ViewModel.GetFilteredRowsForExport()
                .Where(x => !x.IsNewRow)
                .Select(x => (IReadOnlyList<string?>)new string?[]
                {
                    x.Name,
                    x.ContractNo,
                    x.ContactPerson,
                    x.Phone,
                    x.MonthlySettlement.ToString("0.##"),
                    x.SignDate,
                    x.Remark
                })
                .ToList();

            string[] headers = { "供应商名称", "合同编号", "联系人", "联系电话", "月度结算", "签约日期", "备注" };
            var saved = await SimpleListExport.ExportAsync("供应商列表", headers, rows);
            ViewModel.StatusMessage = saved ? "已导出供应商列表。" : "已取消导出。";
        }
        catch (Exception ex)
        {
            // 文件被占用、目标目录无写权限等都会进这里，界面必须给提示而不是崩溃
            ViewModel.StatusMessage = $"导出失败：{ex.Message}";
        }
    }

    /// <summary>弹出确认对话框，确认后删除指定行。</summary>
    /// <param name="row">待删除的记录。</param>
    private async System.Threading.Tasks.Task ConfirmAndDeleteAsync(Supplier row)
    {
        var dialog = new ContentDialog
        {
            // WinUI 3 的对话框必须指定 XamlRoot，否则 ShowAsync 会抛异常
            XamlRoot = XamlRoot,
            Title = "确认删除",
            Content = $"确定删除供应商「{row.Name}」吗？该操作不可撤销。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            LedgerGrid.CancelPendingEdit();
            ViewModel.DeleteRow(row);
        }
    }

    // ------------------------------------------------------------------
    // 合同材料单价明细事件处理
    // ------------------------------------------------------------------

    /// <summary>明细表选中行变化时同步到视图模型。</summary>
    private void OnItemSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ContractItems.Selected = ItemGrid.SelectedItem as SupplierContractItem;
    }

    /// <summary>明细「新增」：插入空白行并进入编辑状态。</summary>
    private void OnItemNewRowClick(object sender, RoutedEventArgs e)
    {
        // 防崩溃：插入新行前先结束上一行的编辑事务，避免编辑态下改动集合
        ItemGrid.CancelPendingEdit();
        ContractItems.NewRow();

        var row = ContractItems.Items.FirstOrDefault(x => x.IsNewRow);
        if (row is null)
        {
            return;
        }

        ItemGrid.SelectedItem = row;
        ItemGrid.CurrentColumn = ItemGrid.Columns[0];
        ItemGrid.ScrollIntoView(row, ItemGrid.Columns[0]);

        ItemGrid.BeginEditWhenReady();
    }

    /// <summary>明细行内「保存」：新增入库或已有行更新。</summary>
    private void OnItemSaveRowClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SupplierContractItem row })
        {
            ItemGrid.CancelPendingEdit();
            ContractItems.SaveRow(row);
        }
    }

    /// <summary>明细行内「取消」：移除未入库的新增行。</summary>
    private void OnItemCancelNewRowClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SupplierContractItem row })
        {
            ItemGrid.CancelPendingEdit();
            ContractItems.CancelNewRow(row);
        }
    }

    /// <summary>明细行内「删除」：二次确认后删除。</summary>
    private async void OnItemDeleteRowClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: SupplierContractItem row })
        {
            return;
        }

        await ConfirmAndDeleteItemAsync(row);
    }

    /// <summary>明细工具栏「删除」：二次确认后删除当前选中行。</summary>
    private async void OnItemDeleteSelectedClick(object sender, RoutedEventArgs e)
    {
        if (ContractItems.Selected is not { } row)
        {
            ContractItems.StatusMessage = "请先在明细表格中选中要删除的行。";
            return;
        }

        await ConfirmAndDeleteItemAsync(row);
    }

    /// <summary>明细「刷新」：按当前供应商重新加载明细。</summary>
    private void OnItemRefreshClick(object sender, RoutedEventArgs e)
    {
        ItemGrid.CancelPendingEdit();
        ContractItems.LoadForSupplier(ContractItems.CurrentSupplierId);
    }

    /// <summary>弹出确认对话框，确认后删除合同材料明细。</summary>
    private async System.Threading.Tasks.Task ConfirmAndDeleteItemAsync(SupplierContractItem row)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "确认删除",
            Content = $"确定删除材料「{row.MaterialName}」（单价 {row.UnitPrice} 元/{row.Unit}）吗？该操作不可撤销。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            ItemGrid.CancelPendingEdit();
            ContractItems.DeleteRow(row);
        }
    }

    // ------------------------------------------------------------------
    // AI 合同解析
    // ------------------------------------------------------------------

    /// <summary>合同 PDF 在数据目录下的相对子目录名，与「供应商合同」页的上传存储约定一致。</summary>
    private const string ContractsSubDir = "Contracts";

    /// <summary>分类日志实例。</summary>
    private readonly ILogger _logger = AppLog.CreateLogger<SupplierListPage>();

    /// <summary>
    /// 「AI 解析合同 PDF」：把选中供应商的合同 PDF 前几页渲染成图片发给在线视觉大模型，
    /// 提取主表信息与材料单价明细后回显到界面，由用户核对编辑后手动保存入库。
    /// 解析失败（PDF 打不开、AI 未配置、请求超时、返回格式错误）一律弹窗提示，不影响页面其他功能。
    /// </summary>
    private async void OnAiParseContractClick(object sender, RoutedEventArgs e)
    {
        var supplier = ViewModel.SelectedSupplier;
        if (supplier is null)
        {
            ContractItems.StatusMessage = "请先在上方表格选中一个供应商，再解析合同。";
            return;
        }

        if (supplier.IsNewRow || supplier.Id == 0)
        {
            ContractItems.StatusMessage = "请先保存该供应商，再解析合同。";
            return;
        }

        var settings = SettingsService.Instance.Settings;
        if (!settings.IsAiConfigured)
        {
            await ShowAiNoticeAsync("尚未配置在线 AI", "请先到「设置」页填写在线 AI 的服务地址、API Key 与模型名称，再使用合同解析。");
            return;
        }

        string pdfPath;
        if (!string.IsNullOrWhiteSpace(supplier.ContractFilePath))
        {
            // 已上传过合同：直接解析数据目录中的已存文件
            var dataRoot = AppPaths.DataRoot ?? Path.Combine(AppContext.BaseDirectory, "Data");
            pdfPath = Path.IsPathRooted(supplier.ContractFilePath)
                ? supplier.ContractFilePath
                : Path.Combine(dataRoot, supplier.ContractFilePath);

            if (!File.Exists(pdfPath))
            {
                await ShowAiNoticeAsync("合同文件不存在", "该供应商的合同文件已丢失或被移动，请先到「供应商合同」页重新上传。");
                return;
            }
        }
        else
        {
            // 尚未上传合同：先选文件并按「供应商合同」页的约定存盘入库，再继续解析
            var picked = await PickAndStoreContractPdfAsync(supplier);
            if (picked is null)
            {
                return;
            }

            pdfPath = picked;
        }

        // 主表可能正处在单元格编辑态，先结束编辑事务再回显，避免绑定刷新与编辑事务冲突
        LedgerGrid.CancelPendingEdit();

        AiParseBtn.IsEnabled = false;
        ContractItems.StatusMessage = "正在解析合同 PDF（视模型速度约需 1~2 分钟），请勿关闭页面…";
        try
        {
            var result = await ContractPdfParser.ParseContractAsync(pdfPath);

            // 主表回显：仅用识别到的非空字段覆盖，识别缺失的字段保留用户已填内容
            if (!string.IsNullOrWhiteSpace(result.SupplierName))
            {
                supplier.Name = result.SupplierName;
            }

            if (!string.IsNullOrWhiteSpace(result.ContractNo))
            {
                supplier.ContractNo = result.ContractNo;
            }

            if (!string.IsNullOrWhiteSpace(result.ContactPerson))
            {
                supplier.ContactPerson = result.ContactPerson;
            }

            if (!string.IsNullOrWhiteSpace(result.Phone))
            {
                supplier.Phone = result.Phone;
            }

            if (!string.IsNullOrWhiteSpace(result.SignDate))
            {
                supplier.SignDate = result.SignDate;
            }

            await ApplyMaterialsAsync(supplier, result);

            ViewModel.StatusMessage = $"合同解析完成：主表字段已填入「{supplier.Name}」行，请核对后点击该行「保存」入库。";
        }
        catch (OperationCanceledException)
        {
            ContractItems.StatusMessage = "合同解析已取消。";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AI 解析合同失败，供应商={SupplierId}。", supplier.Id);
            ContractItems.StatusMessage = "合同解析失败，详细原因见弹窗。";
            await ShowAiNoticeAsync("AI 解析失败", ex.Message);
        }
        finally
        {
            AiParseBtn.IsEnabled = true;
        }
    }

    /// <summary>
    /// 弹出文件选择器选择合同 PDF，复制到数据目录 Contracts/ 下（GUID 命名，规避中文/重名/非法字符问题），
    /// 相对路径写回 Supplier.ContractFilePath 并落库，与「供应商合同」页的上传约定一致。
    /// </summary>
    /// <param name="supplier">已入库的供应商实体。</param>
    /// <returns>复制后的 PDF 绝对路径；用户取消选择时返回 null。</returns>
    private async System.Threading.Tasks.Task<string?> PickAndStoreContractPdfAsync(Supplier supplier)
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

        var file = await picker.PickSingleFileAsync();
        if (file is null)
        {
            ContractItems.StatusMessage = "已取消选择合同文件。";
            return null;
        }

        var dataRoot = AppPaths.DataRoot ?? Path.Combine(AppContext.BaseDirectory, "Data");
        var contractsDir = Path.Combine(dataRoot, ContractsSubDir);
        Directory.CreateDirectory(contractsDir);

        var targetPath = Path.Combine(contractsDir, $"{Guid.NewGuid():N}.pdf");
        using (var source = await file.OpenStreamForReadAsync())
        using (var target = File.Create(targetPath))
        {
            await source.CopyToAsync(target);
        }

        supplier.ContractFilePath = Path.GetRelativePath(dataRoot, targetPath);
        DatabaseService.Instance.UpdateSupplier(supplier);
        ContractItems.StatusMessage = "合同文件已保存，开始解析…";
        return targetPath;
    }

    /// <summary>
    /// 把解析出的材料明细回显到明细表（行数据可双击编辑）。
    /// 明细表已有已入库数据时弹窗询问「覆盖 / 追加」；导入的行保持未保存状态，等用户逐行保存入库。
    /// </summary>
    /// <param name="supplier">明细归属的供应商。</param>
    /// <param name="result">AI 解析结果。</param>
    private async System.Threading.Tasks.Task ApplyMaterialsAsync(Supplier supplier, ContractPdfParser.ContractParseResult result)
    {
        // 解析器已过滤过无效行，这里再按「名称与单价均无」兜底过滤，避免出现导完即被空白行防呆移除的行
        var rows = result.Materials
            .Where(m => !string.IsNullOrWhiteSpace(m.Name) || m.Price > 0)
            .Select(m => new SupplierContractItem
            {
                SupplierId = supplier.Id,
                MaterialName = m.Name,
                Specification = m.Spec,
                Unit = m.Unit,
                UnitPrice = m.Price,
                IsNewRow = true
            })
            .ToList();

        if (rows.Count == 0)
        {
            ContractItems.StatusMessage = "AI 未在合同中识别到材料明细，仅回显了主表信息。";
            return;
        }

        ItemGrid.CancelPendingEdit();

        var existingCount = ContractItems.Items.Count(x => !x.IsNewRow);
        var replaceExisting = false;
        if (existingCount > 0)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "选择导入方式",
                Content = $"当前已有 {existingCount} 条材料明细。\n\n「覆盖」：清空现有明细后导入识别到的 {rows.Count} 条；\n「追加」：保留现有明细，把识别到的 {rows.Count} 条加在后面。",
                PrimaryButtonText = $"覆盖（导入 {rows.Count} 条）",
                SecondaryButtonText = $"追加（导入 {rows.Count} 条）",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary
            };

            var choice = await dialog.ShowAsync();
            if (choice == ContentDialogResult.None)
            {
                ContractItems.StatusMessage = "已取消导入材料明细。";
                return;
            }

            replaceExisting = choice == ContentDialogResult.Primary;
        }

        ItemGrid.CancelPendingEdit();
        ContractItems.ImportRows(supplier.Id, rows, replaceExisting);
    }

    /// <summary>弹出 AI 相关提示/错误对话框（ContentDialog，自动适配页面 XamlRoot）。</summary>
    /// <param name="title">弹窗标题。</param>
    /// <param name="content">弹窗正文。</param>
    private async System.Threading.Tasks.Task ShowAiNoticeAsync(string title, string content)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = content,
            CloseButtonText = "确定",
            DefaultButton = ContentDialogButton.Close
        };

        await dialog.ShowAsync();
    }
}
