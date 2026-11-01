using System;
using System.IO;
using MaterialManagementApp.Models;
using MaterialManagementApp.Services;
using MaterialManagementApp.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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
        // 用当前强调色初始化取色器（ColorPicker 用 Color，配置存的是 hex 字符串）
        if (TryParseHexColor(ViewModel.AccentColor, out var initialColor))
        {
            AccentColorPicker.Color = initialColor;
        }
    }

    /// <summary>预设强调色色块点击：直接写入强调色并即时预览。</summary>
    /// <param name="sender">被点击的色块按钮，Tag 为十六进制颜色字符串。</param>
    /// <param name="e">路由事件参数。</param>
    private void OnAccentSwatchClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string hex })
        {
            ViewModel.AccentColor = hex;
            if (TryParseHexColor(hex, out var color))
            {
                AccentColorPicker.Color = color;
            }
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

    /// <summary>「添加」项目部。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">路由事件参数。</param>
    private void OnAddDeptClick(object sender, RoutedEventArgs e)
    {
        ViewModel.AddProjectDept();
    }

    /// <summary>项目部行内「保存」：重命名并级联更新业务记录。</summary>
    /// <param name="sender">被点击的按钮（DataContext 为该行项目部）。</param>
    /// <param name="e">路由事件参数。</param>
    private void OnSaveDeptClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ProjectDept dept })
        {
            ViewModel.RenameProjectDept(dept);
        }
    }

    /// <summary>项目部行内「删除」：二次确认后删除；仍被引用时由视图模型拦截。</summary>
    /// <param name="sender">被点击的按钮（DataContext 为该行项目部）。</param>
    /// <param name="e">路由事件参数。</param>
    private async void OnDeleteDeptClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ProjectDept dept })
        {
            return;
        }

        var dialog = new ContentDialog
        {
            // WinUI 3 的对话框必须指定 XamlRoot，否则 ShowAsync 会抛异常
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

    /// <summary>「添加」期数。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">路由事件参数。</param>
    private void OnAddPhaseClick(object sender, RoutedEventArgs e)
    {
        ViewModel.AddProjectPhase();
    }

    /// <summary>期数行内「保存」：重命名并级联更新业务记录。</summary>
    /// <param name="sender">被点击的按钮（DataContext 为该行期数）。</param>
    /// <param name="e">路由事件参数。</param>
    private void OnSavePhaseClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ProjectPhase phase })
        {
            ViewModel.RenameProjectPhase(phase);
        }
    }

    /// <summary>期数行内「删除」：二次确认后删除；仍被引用时由视图模型拦截。</summary>
    /// <param name="sender">被点击的按钮（DataContext 为该行期数）。</param>
    /// <param name="e">路由事件参数。</param>
    private async void OnDeletePhaseClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ProjectPhase phase })
        {
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
