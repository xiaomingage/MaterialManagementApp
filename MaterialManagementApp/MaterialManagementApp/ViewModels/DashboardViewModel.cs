using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MaterialManagementApp.Common;
using MaterialManagementApp.Models;
using MaterialManagementApp.Services;
using Microsoft.Extensions.Logging;

namespace MaterialManagementApp.ViewModels;

/// <summary>
/// 工作台（首页）的视图模型。负责台账概况四项统计与快捷操作提示。
/// </summary>
public partial class DashboardViewModel : ObservableObject
{
    /// <summary>分类日志实例，输出到 IDE 输出窗口（Debug Provider）。</summary>
    private readonly ILogger _logger = AppLog.CreateLogger<DashboardViewModel>();

    /// <summary>启动自检提示是否已在本次运行中读取过（避免切换页面时再次弹出）。</summary>
    private bool _startupWarningLoaded;

    /// <summary>
    /// 构造函数。partial property 不允许字段初始化器，
    /// 因此把不可空字符串的默认值放在这里赋值，避免 CS8618 警告。
    /// </summary>
    public DashboardViewModel()
    {
        StatusMessage = string.Empty;
    }

    /// <summary>地材记录条数（MaterialLedger 表行数）。</summary>
    [ObservableProperty]
    private int _earthMaterialCount;

    /// <summary>Excel 待同步条数。Excel 导出/同步模块尚未实现，恒为 0。</summary>
    [ObservableProperty]
    private int _excelPendingCount;

    /// <summary>甲供物资低库存预警种类数。</summary>
    [ObservableProperty]
    private int _partyALowStockCount;

    /// <summary>机具在场台数（Equipment 表中状态不为「已退场」的行数）。</summary>
    [ObservableProperty]
    private int _equipmentOnSiteCount;

    /// <summary>状态提示文本，用于反馈统计加载失败或未实现功能的提示。</summary>
    [ObservableProperty]
    private string _statusMessage;

    /// <summary>今日扫描到的施工记录（含待定位），供「今日施工记录」列表展示。</summary>
    public ObservableCollection<SiteProgressDaily> TodayRecords { get; } = new();

    /// <summary>推送给高德地图 WebView2 的 JSON（工点点位 + 状态语义 + Key）。</summary>
    [ObservableProperty]
    private string _mapPayload = "{}";

    /// <summary>施工进度概况文案，展示扫描张数、覆盖工点与状态色含义。</summary>
    [ObservableProperty]
    private string _constructionSummary = string.Empty;

    /// <summary>待定位池照片条数（未提取到经纬度，已保留但未上图）。</summary>
    [ObservableProperty]
    private int _pendingLocationCount;

    /// <summary>待定位池提示文案，数量为 0 时为空串（由转换器隐藏容器）。</summary>
    [ObservableProperty]
    private string _pendingLocationHint = string.Empty;

    /// <summary>今日无记录时的空状态提示；有记录时为空串。</summary>
    [ObservableProperty]
    private string _todayRecordsHint = string.Empty;

    /// <summary>启动自检失败提示（多条换行合并）；全部通过时为空串。</summary>
    [ObservableProperty]
    private string _startupWarning = string.Empty;

    /// <summary>启动自检提示条是否可见，用户点击「知道了」后隐藏。</summary>
    [ObservableProperty]
    private bool _isStartupWarningVisible;

    /// <summary>关闭启动自检提示条（本次运行内不再显示）。</summary>
    [RelayCommand]
    public void DismissStartupWarning() => IsStartupWarningVisible = false;

    /// <summary>扫描进度文本，如「正在扫描 128/500」或「扫描完成（今日新增 12 条）」。</summary>
    [ObservableProperty]
    private string _scanProgressText = string.Empty;

    /// <summary>扫描进度文本是否可见。</summary>
    [ObservableProperty]
    private bool _isScanProgressVisible;

    /// <summary>加载台账概况统计。查询失败时记录提示而不抛出，保证首页始终可打开。</summary>
    [RelayCommand]
    public void Load()
    {
        _logger.LogInformation("正在加载仪表盘统计数据...");

        // 启动自检提示只在本次运行首次加载时读取一次，用户关闭后切换页面不再重复弹出
        if (!_startupWarningLoaded)
        {
            _startupWarningLoaded = true;
            var warnings = StartupSelfCheckService.Messages;
            if (warnings.Count > 0)
            {
                StartupWarning = string.Join(Environment.NewLine, warnings);
                IsStartupWarningVisible = true;
            }
        }

        try
        {
            EarthMaterialCount = (int)DatabaseService.Instance.ExecuteScalarCount(
                "SELECT COUNT(*) FROM MaterialLedger WHERE IsDeleted = 0;");

            // Excel 待同步需要「已导出/未同步」状态字段支撑，当前无对应表结构，先固定为 0
            ExcelPendingCount = 0;

            // 阈值改由设置页维护，避免不同项目用不同标准时还要改代码
            var lowStockThreshold = SettingsService.Instance.Settings.LowStockThreshold;

            // 甲供物资库存量 = 入库 - 出库 + 退库，低于阈值即视为预警
            PartyALowStockCount = (int)DatabaseService.Instance.ExecuteScalarCount(
                "SELECT COUNT(*) FROM PartyAMaterial WHERE (InboundQty - OutboundQty + ReturnQty) <= @threshold;",
                new Dictionary<string, object?> { ["@threshold"] = lowStockThreshold });

            EquipmentOnSiteCount = (int)DatabaseService.Instance.ExecuteScalarCount(
                "SELECT COUNT(*) FROM Equipment WHERE Status <> @retired;",
                new Dictionary<string, object?> { ["@retired"] = "已退场" });

            StatusMessage = string.Empty;
            _logger.LogInformation("仪表盘统计数据加载完成。");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "加载仪表盘统计数据失败。");
            StatusMessage = $"统计加载失败：{UserErrorText.Describe(ex)}";
        }
    }

    /// <summary>
    /// 刷新施工进度数据：重新生成地图 JSON、当日记录列表与待定位池提示。
    /// 每次进入首页、以及后台扫描入库完成后调用；查询失败只提示，不抛出。
    /// </summary>
    public void RefreshConstructionData()
    {
        // 地图聚合与今日记录分开容错：任一失败只清空自己，不连累另一块
        try
        {
            MapPayload = SiteProgressService.Instance.BuildMapPayload();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "生成地图数据失败，本次推送空地图。");
            MapPayload = "{}";
        }

        try
        {
            var today = DateTime.Today.ToString("yyyy-MM-dd");
            var rows = DatabaseService.Instance.GetSiteProgressForDate(today)
                .OrderBy(row => row.IsLocated ? 0 : 1)
                .ThenByDescending(row => row.LastShotAt)
                .ToList();

            // 今日复工标记：已完工工点又扫到照片并自动恢复在建时，列表该行黄底提示
            var overrides = SiteStatusService.Instance.GetOverrides();
            var todayDate = DateTime.Today;

            TodayRecords.Clear();
            foreach (var row in rows)
            {
                row.IsRevived = overrides.TryGetValue(row.SiteName, out var statusOverride)
                                && SiteStatusService.IsRevivedToday(statusOverride, todayDate);
                TodayRecords.Add(row);
            }

            PendingLocationCount = DatabaseService.Instance.CountPendingLocationProgress();
            PendingLocationHint = PendingLocationCount > 0
                ? $"待定位池：{PendingLocationCount} 张照片未识别到经纬度，记录已保留、暂未上图。"
                : string.Empty;
            TodayRecordsHint = rows.Count == 0
                ? "今日还没有扫描到水印照片。配置根目录后，把「今日水印相机」的照片文件夹放进去即可自动解析。"
                : string.Empty;

            var locatedSiteCount = rows.Where(row => row.IsLocated).Select(row => row.SiteName).Distinct().Count();
            ConstructionSummary = rows.Count == 0
                ? "暂无今日扫描记录。"
                : $"今日扫描 {rows.Sum(row => row.PhotoCount)} 张照片，覆盖 {locatedSiteCount} 个工点；" +
                  "状态色：绿色作业中 / 黄色停滞 1–2 天 / 红色中断 3 天以上 / 灰色已完工。";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "加载施工进度数据失败。");
            ConstructionSummary = $"施工进度加载失败：{UserErrorText.Describe(ex)}";
        }
    }

    /// <summary>提示某个快捷功能尚未实现。</summary>
    /// <param name="feature">功能名称。</param>
    public void ShowNotImplemented(string feature)
    {
        StatusMessage = $"「{feature}」功能尚未实现，后续版本提供。";
    }
}
