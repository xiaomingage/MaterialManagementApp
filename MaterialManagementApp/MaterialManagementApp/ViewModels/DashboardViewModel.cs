using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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

    /// <summary>加载台账概况统计。查询失败时记录提示而不抛出，保证首页始终可打开。</summary>
    [RelayCommand]
    public void Load()
    {
        _logger.LogInformation("正在加载仪表盘统计数据...");
        try
        {
            EarthMaterialCount = (int)DatabaseService.Instance.ExecuteScalarCount(
                "SELECT COUNT(*) FROM MaterialLedger;");

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
            StatusMessage = $"统计加载失败：{ex.Message}";
        }
    }

    /// <summary>提示某个快捷功能尚未实现。</summary>
    /// <param name="feature">功能名称。</param>
    public void ShowNotImplemented(string feature)
    {
        StatusMessage = $"「{feature}」功能尚未实现，后续版本提供。";
    }
}
