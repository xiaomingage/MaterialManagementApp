using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using MaterialManagementApp.Models;
using MaterialManagementApp.Services;
using Microsoft.Extensions.Logging;

namespace MaterialManagementApp.ViewModels;

/// <summary>
/// 合同材料明细（SupplierContractItem）的视图模型。
/// 负责加载、编辑、增删改指定供应商下的合同材料价格。
/// </summary>
public sealed class ContractItemViewModel : INotifyPropertyChanged
{
    private readonly ILogger _logger = AppLog.CreateLogger<ContractItemViewModel>();

    private ObservableCollection<SupplierContractItem> _items = [];
    private SupplierContractItem? _selected;
    private string _statusMessage = "请选择上方的供应商查看合同材料明细。";
    private int? _currentSupplierId;

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<SupplierContractItem> Items
    {
        get => _items;
        private set => SetField(ref _items, value);
    }

    public SupplierContractItem? Selected
    {
        get => _selected;
        set => SetField(ref _selected, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set => SetField(ref _statusMessage, value);
    }

    public int? CurrentSupplierId => _currentSupplierId;

    /// <summary>根据供应商主键加载其合同材料明细；null 时清空列表。</summary>
    public void LoadForSupplier(int? supplierId)
    {
        _currentSupplierId = supplierId;

        if (supplierId is null)
        {
            Items = [];
            StatusMessage = "请选择上方的供应商查看合同材料明细。";
            _logger.LogDebug("合同材料明细清空（未选中供应商）。");
            return;
        }

        _logger.LogInformation("正在加载供应商 {SupplierId} 的合同材料明细...", supplierId.Value);

        try
        {
            var items = DatabaseService.Instance.GetContractItemsBySupplier(supplierId.Value);
            Items = new ObservableCollection<SupplierContractItem>(items);
            StatusMessage = $"共 {items.Count} 条合同材料明细。";
            _logger.LogInformation("合同材料明细加载完成，共 {Count} 条。", items.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "加载合同材料明细失败，SupplierId={SupplierId}。", supplierId.Value);
            StatusMessage = "加载合同材料明细失败，请检查日志。";
        }
    }

    /// <summary>在当前供应商下新增一行空白明细，等待内联编辑后保存。</summary>
    public void NewRow()
    {
        if (_currentSupplierId is null)
        {
            StatusMessage = "请先在上方的供应商表格中选中一行，再新增材料明细。";
            _logger.LogWarning("新增合同材料被拒绝：未选中供应商。");
            return;
        }

        var pending = Items.FirstOrDefault(x => x.IsNewRow);
        if (pending is not null)
        {
            StatusMessage = "当前已有未保存的空白行，请先保存或取消后再新增。";
            _logger.LogWarning("新增合同材料被拒绝：存在未保存的空白行。");
            return;
        }

        var row = new SupplierContractItem
        {
            SupplierId = _currentSupplierId.Value,
            IsNewRow = true
        };
        Items.Insert(0, row);
        Selected = row;
        StatusMessage = "已插入空白行，请填写材料名称、规格、单位、单价后点击行末「保存」。";
        _logger.LogInformation("已在明细表首行插入空白行，供应商={SupplierId}。", _currentSupplierId.Value);
    }

    /// <summary>保存当前编辑的行（新增或更新）。</summary>
    public void SaveRow(SupplierContractItem? row)
    {
        if (row is null) return;

        var isInsert = row.IsNewRow;
        if (string.IsNullOrWhiteSpace(row.MaterialName))
        {
            StatusMessage = "材料名称不能为空，请填写后再保存。";
            _logger.LogWarning("保存合同材料被拒绝：材料名称为空。");
            return;
        }

        try
        {
            if (isInsert)
            {
                var id = DatabaseService.Instance.AddContractItem(row);
                row.Id = id;
                row.IsNewRow = false;
                StatusMessage = "新增合同材料已保存。";
                _logger.LogInformation("新增合同材料成功，Id={Id}，供应商={SupplierId}，材料={Name}，单价={Price}元/{Unit}。",
                    id, row.SupplierId, row.MaterialName, row.UnitPrice, row.Unit);
            }
            else
            {
                DatabaseService.Instance.UpdateContractItem(row);
                StatusMessage = "合同材料已更新。";
                _logger.LogInformation("更新合同材料成功，Id={Id}，材料={Name}，单价={Price}元/{Unit}。",
                    row.Id, row.MaterialName, row.UnitPrice, row.Unit);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "保存合同材料失败，Id={Id}，材料={Name}。", row.Id, row.MaterialName);
            StatusMessage = "保存合同材料失败，请检查日志。";
        }
    }

    /// <summary>取消未保存的新增行，直接从列表移除。</summary>
    public void CancelNewRow(SupplierContractItem? row)
    {
        if (row is null || !row.IsNewRow) return;
        Items.Remove(row);
        StatusMessage = "已取消新增。";
        _logger.LogInformation("已移除未保存的明细新增行。");
    }

    /// <summary>删除已有明细行。</summary>
    public void DeleteRow(SupplierContractItem? row)
    {
        if (row is null || row.IsNewRow) return;

        try
        {
            DatabaseService.Instance.DeleteContractItem(row.Id);
            Items.Remove(row);
            StatusMessage = "已删除合同材料。";
            _logger.LogInformation("删除合同材料成功，Id={Id}，材料={Name}。", row.Id, row.MaterialName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "删除合同材料失败，Id={Id}。", row.Id);
            StatusMessage = "删除合同材料失败，请检查日志。";
        }
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
