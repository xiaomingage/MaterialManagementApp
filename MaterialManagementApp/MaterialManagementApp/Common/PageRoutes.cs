using System;
using System.Collections.Generic;
using MaterialManagementApp.Pages;

namespace MaterialManagementApp.Common;

/// <summary>
/// 导航路由键与页面类型的集中映射表。
/// 菜单项只保存路由键（Tag），页面类型解析集中在此处，
/// 后续把占位页替换为真实页面时只需修改本类，无需改动 XAML。
/// </summary>
public static class PageRoutes
{
    /// <summary>工作台路由键。</summary>
    public const string Dashboard = "dashboard";

    /// <summary>设置页路由键。</summary>
    public const string Settings = "settings";

    /// <summary>工作日志路由键。</summary>
    public const string WorkLog = "worklog";

    /// <summary>地材台账 - 台账明细。</summary>
    public const string EarthLedger = "earth-ledger";

    /// <summary>地材台账 - 渣土运输。</summary>
    public const string EarthTransport = "earth-transport";

    /// <summary>供应商管理 - 供应商合同。</summary>
    public const string SupplierContract = "supplier-contract";

    /// <summary>供应商管理 - 供应商列表。</summary>
    public const string SupplierList = "supplier-list";

    /// <summary>供应商管理 - 月度结算。</summary>
    public const string SupplierSettlement = "supplier-settlement";

    /// <summary>甲供物资 - 物资申请档案。</summary>
    public const string PartyAApply = "partya-apply";

    /// <summary>甲供物资 - 甲供物资台账（由入库验收/出库领用/退库记录/库存查询合并）。</summary>
    public const string PartyALedger = "partya-ledger";

    /// <summary>自购物资 - 物资申请表。</summary>
    public const string SelfApply = "self-apply";

    /// <summary>自购物资 - 物资采购明细。</summary>
    public const string SelfPurchase = "self-purchase";

    /// <summary>库存管理 - 入库领用退库。</summary>
    public const string StockInOut = "stock-inout";

    /// <summary>库存管理 - 库存查询。</summary>
    public const string StockCheck = "stock-check";

    /// <summary>库存管理 - 库存预警。</summary>
    public const string StockWarning = "stock-warning";

    /// <summary>机具设备 - 机具入场。</summary>
    public const string EquipEntry = "equip-entry";

    /// <summary>机具设备 - 小型机具明细。</summary>
    public const string EquipSmall = "equip-small";

    /// <summary>机具设备 - 工程机械。</summary>
    public const string EquipMachine = "equip-machine";

    /// <summary>机具设备 - 维保与年检。</summary>
    public const string EquipMaintain = "equip-maintain";

    /// <summary>设备租赁 - 设备租赁台账。</summary>
    public const string RentalLedger = "rental-ledger";

    /// <summary>物资核销 - 月度核销。</summary>
    public const string WriteOffMonthly = "writeoff-monthly";

    /// <summary>物资核销 - 核销台账。</summary>
    public const string WriteOffLedger = "writeoff-ledger";

    /// <summary>
    /// 路由键到显示标题的映射。占位页需要正确的标题文案，
    /// 因此这里覆盖全部二级菜单，而不是仅覆盖已实现的页面。
    /// </summary>
    private static readonly Dictionary<string, string> _titles = new(StringComparer.OrdinalIgnoreCase)
    {
        [Dashboard] = "工作台",
        [WorkLog] = "工作日志",
        [EarthLedger] = "台账明细",
        [EarthTransport] = "渣土运输",
        [SupplierContract] = "供应商合同",
        [SupplierList] = "供应商列表",
        [SupplierSettlement] = "月度结算",
        [PartyAApply] = "物资申请档案",
        [PartyALedger] = "甲供物资台账",
        [SelfApply] = "物资申请表",
        [SelfPurchase] = "物资采购明细",
        [StockInOut] = "入库领用退库",
        [StockCheck] = "库存查询",
        [StockWarning] = "库存预警",
        [EquipEntry] = "机具入场",
        [EquipSmall] = "小型机具明细",
        [EquipMachine] = "工程机械",
        [EquipMaintain] = "维保与年检",
        [RentalLedger] = "设备租赁台账",
        [WriteOffMonthly] = "月度核销",
        [WriteOffLedger] = "核销台账"
    };

    /// <summary>
    /// 解析路由键对应的页面类型与标题。已实现的页面返回真实类型，
    /// 其余一律回退到占位页，保证菜单可点击且不会因缺少页面而报错。
    /// </summary>
    /// <param name="route">NavigationViewItem 的 Tag 值，即路由键。</param>
    /// <returns>页面类型与显示标题的元组；路由未知时返回占位页与兜底标题。</returns>
    public static (Type PageType, string Title) Resolve(string route)
    {
        var key = route ?? string.Empty;
        var title = _titles.TryGetValue(key, out var t) ? t : "功能页面";

        return key.ToLowerInvariant() switch
        {
            // 通用
            Dashboard => (typeof(DashboardPage), title),
            WorkLog => (typeof(WorkLogPage), title),
            Settings => (typeof(SettingsPage), title),

            // 地材台账
            EarthLedger => (typeof(MaterialLedgerPage), title),
            EarthTransport => (typeof(SlagTransportPage), title),

            // 供应商管理
            SupplierContract => (typeof(SupplierContractPage), title),
            SupplierList => (typeof(SupplierListPage), title),
            SupplierSettlement => (typeof(SupplierSettlementPage), title),

            // 甲供物资
            PartyAApply => (typeof(PartyAArchivePage), title),
            PartyALedger => (typeof(PartyALedgerPage), title),

            // 自购物资
            SelfApply => (typeof(SelfPurchaseApplyPage), title),
            SelfPurchase => (typeof(SelfPurchaseDetailPage), title),

            // 库存管理
            StockInOut => (typeof(StockInOutPage), title),
            StockCheck => (typeof(StockCheckPage), title),
            StockWarning => (typeof(StockWarningPage), title),

            // 机具设备
            EquipEntry => (typeof(ExcavatorEntryPage), title),
            EquipSmall => (typeof(SmallEquipmentPage), title),
            EquipMachine => (typeof(EquipmentPage), title),
            EquipMaintain => (typeof(EquipmentMaintenancePage), title),

            // 设备租赁
            RentalLedger => (typeof(RentalDevicePage), title),

            // 物资核销
            WriteOffMonthly => (typeof(WriteOffMonthlyPage), title),
            WriteOffLedger => (typeof(WriteOffLedgerPage), title),

            _ => (typeof(PlaceholderPage), title)
        };
    }
}
