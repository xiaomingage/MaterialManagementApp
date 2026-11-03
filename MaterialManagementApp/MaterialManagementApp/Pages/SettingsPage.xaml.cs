using System;
using System.IO;
using System.Threading.Tasks;
using MaterialManagementApp.Models;
using MaterialManagementApp.Services;
using MaterialManagementApp.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.UI;

namespace MaterialManagementApp.Pages;

/// <summary>
/// 设置页面。提供库存预警阈值、单据默认值、数据库备份与界面主题四类配置。
/// </summary>
public sealed partial class SettingsPage : Page
{
    /// <summary>页面视图模型，供 XAML 通过 x:Bind 绑定。</summary>
    public SettingsViewModel ViewModel { get; } = new();

    /// <summary>初始化设置页面。</summary>
    public SettingsPage()
    {
        InitializeComponent();

        // NumberBox.Value 为 double、ViewModel 属性为 int，x:Bind 不做隐式数值转换，
        // 因此该控件使用经典 Binding，必须设置 DataContext 才能生效
        DataContext = ViewModel;
    }

    /// <summary>
    /// 页面加载完成时用当前强调色初始化取色器。
    /// 数据库与配置文件路径已改为 x:Bind 绑定视图模型展示，无需在此赋值。
    /// </summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">路由事件参数。</param>
    private void Page_Loaded(object sender, RoutedEventArgs e)
    {
        // 取色器初值由 AccentPickerColor 绑定自动同步；分类切换由 SelectedSection 驱动，
        // 页面加载后无需任何代码干预（模板内控件不再生成页面字段）
    }

    /// <summary>预设强调色色块点击：直接写入强调色并即时预览。</summary>
    /// <param name="sender">被点击的色块按钮，Tag 为十六进制颜色字符串。</param>
    /// <param name="e">路由事件参数。</param>
    private void OnAccentSwatchClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string hex })
        {
            ViewModel.AccentColor = hex;
            // 取色器通过 AccentPickerColor 绑定自动跟随强调色，无需手动设置
        }
    }

    /// <summary>自定义取色器颜色变化：把 Color 转回 hex 字符串存入配置并即时预览。</summary>
    /// <param name="sender">取色器。</param>
    /// <param name="args">新旧颜色。</param>
    private void OnAccentColorPickerColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        var c = args.NewColor;
        ViewModel.AccentColor = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
    }

    /// <summary>手动备份：对当前数据库立即创建一份备份，结果显示在卡片状态文本。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">路由事件参数。</param>
    private void OnManualBackupClick(object sender, RoutedEventArgs e)
    {
        var dbPath = DatabaseService.Instance.DatabasePath;
        var backup = DatabaseBackupService.CreateBackup(dbPath, "手动备份");
        ViewModel.BackupStatus = backup is null
            ? "备份失败，请查看日志（Logs\\system-*.log）。"
            : $"备份完成：{backup}";
    }

    /// <summary>从备份恢复：选择备份文件，确认后覆盖当前数据库并退出软件（重启后生效）。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">路由事件参数。</param>
    private async void OnRestoreBackupClick(object sender, RoutedEventArgs e)
    {
        var backups = DatabaseBackupService.GetBackups();
        if (backups.Count == 0)
        {
            ViewModel.BackupStatus = "暂无可用的备份文件。";
            return;
        }

        var list = new ListView
        {
            SelectionMode = ListViewSelectionMode.Single,
            MaxHeight = 240
        };
        foreach (var backup in backups)
        {
            list.Items.Add(new ListViewItem
            {
                Content = $"{Path.GetFileName(backup)}（{File.GetLastWriteTime(backup):yyyy-MM-dd HH:mm:ss}）",
                Tag = backup
            });
        }
        list.SelectedIndex = 0;

        var hint = new TextBlock
        {
            Text = "请选择要恢复的备份文件：",
            Margin = new Thickness(0, 0, 0, 8),
            TextWrapping = TextWrapping.Wrap
        };
        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(hint);
        panel.Children.Add(list);

        var pickDialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "从备份恢复",
            Content = panel,
            PrimaryButtonText = "下一步",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };

        if (await pickDialog.ShowAsync() != ContentDialogResult.Primary || list.SelectedItem is not ListViewItem { Tag: string chosen })
        {
            return;
        }

        var confirmDialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "确认恢复",
            Content = "恢复将覆盖当前数据库，未备份的最新数据可能丢失。恢复完成后软件会自动退出，请重新启动。确定继续吗？",
            PrimaryButtonText = "确定恢复",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };

        if (await confirmDialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var dbPath = DatabaseService.Instance.DatabasePath;
        if (DatabaseBackupService.Restore(chosen, dbPath, out var error))
        {
            var doneDialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "恢复完成",
                Content = "数据库已从备份恢复，软件即将退出。请重新启动软件使用恢复后的数据。",
                CloseButtonText = "确定",
                DefaultButton = ContentDialogButton.Close
            };
            await doneDialog.ShowAsync();
            Application.Current.Exit();
        }
        else
        {
            ViewModel.BackupStatus = $"恢复失败：{error}";
        }
    }

    /// <summary>把 #RRGGBB / #AARRGGBB 解析为 <see cref="Color"/>，失败返回 false。</summary>
    private static bool TryParseHexColor(string? hex, out Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(hex))
        {
            return false;
        }
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
            color = Color.FromArgb(
                a,
                Convert.ToByte(s.Substring(offset, 2), 16),
                Convert.ToByte(s.Substring(offset + 2, 2), 16),
                Convert.ToByte(s.Substring(offset + 4, 2), 16));
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>弹出「新增/编辑」输入弹窗，返回用户确认后的名称；取消返回 null。</summary>
    /// <param name="title">弹窗标题（如「新增项目部」）。</param>
    /// <param name="initialName">编辑时预填的原始名称；新增传空。</param>
    /// <returns>确认后的名称；取消或内容为空时返回 null。</returns>
    private async Task<string?> ShowNameEditDialogAsync(string title, string initialName)
    {
        var input = new TextBox
        {
            Text = initialName,
            PlaceholderText = "请输入名称",
            MaxLength = 50
        };
        // 预填时全选，方便直接改名
        input.SelectAll();
        input.Focus(FocusState.Programmatic);

        var dialog = new ContentDialog
        {
            // WinUI 3 的对话框必须指定 XamlRoot，否则 ShowAsync 会抛异常
            XamlRoot = XamlRoot,
            Title = title,
            Content = input,
            PrimaryButtonText = "确定",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return null;
        }

        var name = input.Text?.Trim();
        return string.IsNullOrEmpty(name) ? null : name;
    }

    /// <summary>「新增项目部」：弹窗收集名称后入库。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">路由事件参数。</param>
    private async void OnAddDeptClick(object sender, RoutedEventArgs e)
    {
        var name = await ShowNameEditDialogAsync("新增项目部", string.Empty);
        if (name is null)
        {
            return;
        }

        ViewModel.NewDeptName = name;
        ViewModel.AddProjectDept();
    }

    /// <summary>「编辑」项目部：弹窗改名后级联更新业务记录；双击表格行等效。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">路由事件参数。</param>
    private async void OnEditDeptClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedDept is not ProjectDept dept)
        {
            ViewModel.StatusMessage = "请先在表格中选中要编辑的项目部。";
            return;
        }

        var name = await ShowNameEditDialogAsync($"编辑项目部「{dept.Name}」", dept.Name);
        if (name is null)
        {
            return;
        }

        // VM 约定：Name 为新值、OriginalName 保留旧值，重命名成功后级联更新业务记录
        dept.Name = name;
        ViewModel.RenameProjectDept(dept);
    }

    /// <summary>双击项目部表格行进入编辑。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">路由事件参数。</param>
    private void OnDeptGridDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        OnEditDeptClick(sender, e);
    }

    /// <summary>「删除」项目部：二次确认后删除；仍被引用时由视图模型拦截。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">路由事件参数。</param>
    private async void OnDeleteDeptClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedDept is not ProjectDept dept)
        {
            ViewModel.StatusMessage = "请先在表格中选中要删除的项目部。";
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "确认删除",
            Content = $"确定删除项目部「{dept.Name}」吗？若仍有记录引用将被拒绝。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            ViewModel.DeleteProjectDept(dept);
        }
    }

    /// <summary>「新增期数」：弹窗收集名称后入库。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">路由事件参数。</param>
    private async void OnAddPhaseClick(object sender, RoutedEventArgs e)
    {
        var name = await ShowNameEditDialogAsync("新增期数", string.Empty);
        if (name is null)
        {
            return;
        }

        ViewModel.NewPhaseName = name;
        ViewModel.AddProjectPhase();
    }

    /// <summary>「编辑」期数：弹窗改名后级联更新业务记录；双击表格行等效。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">路由事件参数。</param>
    private async void OnEditPhaseClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedPhase is not ProjectPhase phase)
        {
            ViewModel.StatusMessage = "请先在表格中选中要编辑的期数。";
            return;
        }

        var name = await ShowNameEditDialogAsync($"编辑期数「{phase.Name}」", phase.Name);
        if (name is null)
        {
            return;
        }

        phase.Name = name;
        ViewModel.RenameProjectPhase(phase);
    }

    /// <summary>双击期数表格行进入编辑。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">路由事件参数。</param>
    private void OnPhaseGridDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        OnEditPhaseClick(sender, e);
    }

    /// <summary>「删除」期数：二次确认后删除；仍被引用时由视图模型拦截。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">路由事件参数。</param>
    private async void OnDeletePhaseClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedPhase is not ProjectPhase phase)
        {
            ViewModel.StatusMessage = "请先在表格中选中要删除的期数。";
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "确认删除",
            Content = $"确定删除期数「{phase.Name}」吗？若仍有记录引用将被拒绝。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            ViewModel.DeleteProjectPhase(phase);
        }
    }

    /// <summary>打开配置文件所在文件夹（资源管理器中选中该文件）。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">路由事件参数。</param>
    private void OnOpenSettingsFolderClick(object sender, RoutedEventArgs e)
    {
        var path = ViewModel.SettingsFilePath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            ViewModel.StatusMessage = "配置文件不存在，无法打开所在文件夹。";
            return;
        }

        // /select 参数会打开文件夹并高亮选中文件，用户可直接看到 settings.json
        System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"");
    }

    /// <summary>适用栏目复选框切换：启用即补列迁移，停用仅隐藏界面。</summary>
    /// <param name="sender">被点击的复选框（DataContext 为该行栏目配置）。</param>
    /// <param name="e">路由事件参数。</param>
    private void OnDeptPhaseTableToggleClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: DeptPhaseApplicableTable table })
        {
            ViewModel.ApplyDeptPhaseTableToggle(table);
        }
    }
}

/// <summary>
/// 设置页分类模板选择器：按 <see cref="SettingsSection.Key"/> 返回右侧内容区的 DataTemplate。
/// 必须是命名空间顶层的非嵌套类型，XAML 编译器才能通过 local: 前缀解析；
/// 模板内容编译期固定（见 SettingsPage.xaml 的 Page.Resources），切换分类不重建页面。
/// </summary>
public sealed class SettingsSectionTemplateSelector : DataTemplateSelector
{
    /// <summary>「通用与外观」分类模板。</summary>
    public DataTemplate? General { get; set; }

    /// <summary>「AI 与识别」分类模板。</summary>
    public DataTemplate? Ai { get; set; }

    /// <summary>「业务与参数」分类模板。</summary>
    public DataTemplate? Business { get; set; }

    /// <summary>「数据与备份」分类模板。</summary>
    public DataTemplate? Data { get; set; }

    /// <summary>按分类键返回对应模板；未知键回退到通用与外观。</summary>
    /// <param name="item">当前选中的 <see cref="SettingsSection"/>。</param>
    /// <param name="container">绑定的容器元素。</param>
    /// <returns>匹配的 DataTemplate。</returns>
    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container)
    {
        return item is SettingsSection section
            ? section.Key switch
            {
                "ai" => Ai,
                "business" => Business,
                "data" => Data,
                _ => General
            }
            : base.SelectTemplateCore(item, container);
    }
}
