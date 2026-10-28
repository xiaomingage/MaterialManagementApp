using System;
using System.Linq;
using MaterialManagementApp.Models;
using MaterialManagementApp.Services.Export;
using MaterialManagementApp.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace MaterialManagementApp.Pages;

/// <summary>
/// 小型机具台账页面（机具设备 → 小型机具明细）。
/// 上半部分为查询筛选面板，下半部分为可编辑表格：
/// 「新增」在表格首行插入一条全编辑状态的空白行，行末「保存」校验后入库、「取消」直接移除。
/// 保留按三层表头模板导出与从模板导入，以及行内查看照片。
/// </summary>
public sealed partial class SmallEquipmentPage : Page
{
    /// <summary>页面视图模型，供 XAML 通过 x:Bind 绑定。</summary>
    public SmallEquipmentViewModel ViewModel { get; } = new();

    /// <summary>初始化页面。</summary>
    public SmallEquipmentPage()
    {
        InitializeComponent();

        // ComboBox.SelectedItem 是 object，无法用 x:Bind 双向绑定到 string 属性，
        // 因此查询面板的下拉框使用经典 Binding，必须设置 DataContext 才能生效
        DataContext = ViewModel;
    }

    /// <summary>进入页面时按当前筛选条件加载小型机具记录。</summary>
    /// <param name="e">导航事件参数。</param>
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel.LoadCommand.Execute(null);
    }

    /// <summary>表格选中行变化时同步到视图模型，保留选中态。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">选中项变化参数。</param>
    private void OnLedgerSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ViewModel.SelectedItem = LedgerGrid.SelectedItem as SmallEquipment;
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
                // 尚未生成行容器时 BeginEdit 会失败，此时保持选中态即可，不影响手工点击编辑
                System.Diagnostics.Debug.WriteLine($"[SmallEquipmentPage] 自动进入编辑失败：{ex.Message}");
            }
        });
    }

    /// <summary>行内「保存」：新增行执行入库，已有行执行更新。</summary>
    /// <param name="sender">被点击的按钮。</param>
    /// <param name="e">路由事件参数。</param>
    private void OnSaveRowClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SmallEquipment row })
        {
            ViewModel.SaveRow(row);
        }
    }

    /// <summary>行内「取消」：移除尚未入库的空白新增行。</summary>
    /// <param name="sender">被点击的按钮。</param>
    /// <param name="e">路由事件参数。</param>
    private void OnCancelNewRowClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SmallEquipment row })
        {
            ViewModel.CancelNewRow(row);
        }
    }

    /// <summary>行内「删除」：二次确认后从集合与数据库中移除该行。</summary>
    /// <param name="sender">被点击的按钮。</param>
    /// <param name="e">路由事件参数。</param>
    private async void OnDeleteRowClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: SmallEquipment row })
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
        if (ViewModel.SelectedItem is not { } row)
        {
            ViewModel.StatusMessage = "请先在表格中选中要删除的行。";
            return;
        }

        await ConfirmAndDeleteAsync(row);
    }

    /// <summary>行内「查看」：取行对象后交给 ViewModel 打开照片。</summary>
    /// <param name="sender">被点击的按钮。</param>
    /// <param name="e">路由事件参数。</param>
    private void OnOpenPhotoClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SmallEquipment row })
        {
            ViewModel.OpenPhoto(row);
        }
    }

    /// <summary>导出：按三层表头模板把当前列表（不含未保存的空白行）生成单 Sheet Excel。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">路由事件参数。</param>
    private async void OnExportTemplateClick(object sender, RoutedEventArgs e)
    {
        try
        {
            // 导出全部筛选结果而非当前页；尚未保存的空白行不入导出结果，避免把空记录写进 Excel
            var rows = ViewModel.GetFilteredRowsForExport().Where(x => !x.IsNewRow).ToList();
            var saved = await SmallEquipmentTemplateExport.ExportAsync(rows);
            ViewModel.StatusMessage = saved ? "已按模板导出小型机具台账。" : "已取消导出。";
        }
        catch (Exception ex)
        {
            // 文件被占用、目标目录无写权限等都会进这里，界面必须给提示而不是崩溃
            ViewModel.StatusMessage = $"导出失败：{ex.Message}";
        }
    }

    /// <summary>从模板导入小型机具台账：解析后询问追加或清空后导入。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">路由事件参数。</param>
    private async void OnImportTemplateClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var items = await SmallEquipmentTemplateImport.PickAndParseAsync();
            if (items is null)
            {
                ViewModel.StatusMessage = "已取消导入。";
                return;
            }

            if (items.Count == 0)
            {
                ViewModel.StatusMessage = "所选文件中没有可导入的数据行（已跳过标题行、表头行、合计与签字类行）。";
                return;
            }

            var dialog = new ContentDialog
            {
                // WinUI 3 的对话框必须指定 XamlRoot，否则 ShowAsync 会抛异常
                XamlRoot = XamlRoot,
                Title = "确认导入",
                Content = $"共解析到 {items.Count} 条小型机具记录。选择「追加导入」保留现有数据，选择「清空后导入」会先删除现有小型机具台账。",
                PrimaryButtonText = "追加导入",
                SecondaryButtonText = "清空后导入",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary
            };

            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.None)
            {
                ViewModel.StatusMessage = "已取消导入。";
                return;
            }

            var count = SmallEquipmentTemplateImport.Commit(items, result == ContentDialogResult.Secondary);
            ViewModel.Load();

            // Load 内部会改写 StatusMessage，因此成功提示必须放在它之后
            ViewModel.StatusMessage = result == ContentDialogResult.Secondary
                ? $"已清空原数据并导入 {count} 条小型机具记录。"
                : $"已追加导入 {count} 条小型机具记录。";
        }
        catch (Exception ex)
        {
            ViewModel.StatusMessage = $"导入失败：{ex.Message}";
        }
    }

    /// <summary>弹出确认对话框，确认后删除指定行。</summary>
    /// <param name="row">待删除的记录。</param>
    private async System.Threading.Tasks.Task ConfirmAndDeleteAsync(SmallEquipment row)
    {
        var dialog = new ContentDialog
        {
            // WinUI 3 的对话框必须指定 XamlRoot，否则 ShowAsync 会抛异常
            XamlRoot = XamlRoot,
            Title = "确认删除",
            Content = $"确定删除小型机具「{row.Name}」的台账记录吗？该操作不可撤销。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            ViewModel.DeleteRow(row);
        }
    }
}
