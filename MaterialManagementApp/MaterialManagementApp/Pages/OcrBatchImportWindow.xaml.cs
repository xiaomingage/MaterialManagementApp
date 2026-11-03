using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MaterialManagementApp.Models;
using MaterialManagementApp.Services;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.System;

namespace MaterialManagementApp.Pages;

/// <summary>
/// 「批量扫描识别」非模态窗口（台账明细页 → 批量扫描识别）。
/// 一次导入多张地材单图片，每张一个卡片：缩略图、可手动修正的识别全文、
/// 可手动填入的字段区，以及该卡片独立的「保存入台账」按钮——
/// 识别有误改好一张存一张，互不影响。识别由内嵌离线 PaddleOCR 逐张完成，全程不联网。
/// </summary>
public sealed partial class OcrBatchImportWindow : Window
{
    /// <summary>等待系统截图写回剪贴板的轮询间隔（毫秒）。</summary>
    private const int CapturePollIntervalMs = 500;

    /// <summary>等待系统截图写回剪贴板的最大轮询次数，约 30 秒。</summary>
    private const int CapturePollCount = 60;

    /// <summary>分类日志实例。</summary>
    private readonly ILogger _logger = AppLog.CreateLogger<OcrBatchImportWindow>();

    /// <summary>识别队列是否正在逐张处理（同一时间只跑一个队列循环）。</summary>
    private bool _isRecognizing;

    /// <summary>卡片集合，绑定到卡片列表。</summary>
    public ObservableCollection<BatchScanItem> Items { get; } = new();

    /// <summary>初始化窗口并设定合适的初始尺寸。</summary>
    public OcrBatchImportWindow()
    {
        InitializeComponent();

        // Window 不是 FrameworkElement，XAML 里无法用 x:Bind 绑定自身属性，这里在代码里直接挂数据源
        CardList.ItemsSource = Items;

        AppWindow.Resize(new SizeInt32(1360, 920));
        SetStatus("请选择、拖入或粘贴地材单图片；识别完成后逐张核对，改好一张点该卡片的「保存入台账」。");
    }

    // ==================== 取图入口 ====================

    /// <summary>选择本地单据图片，支持一次多选。</summary>
    private async void OnPickImagesClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker();
            foreach (var extension in new[] { ".jpg", ".jpeg", ".png", ".bmp", ".tif", ".tiff" })
            {
                picker.FileTypeFilter.Add(extension);
            }

            // WinUI 3 的文件选择器是 Win32 对话框，必须先绑定本窗口句柄，否则构造后直接抛异常
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));

            var files = await picker.PickMultipleFilesAsync();
            foreach (var file in files)
            {
                await AddImageFileAsync(file.Path, file.Name);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "批量扫描选择图片失败。");
            SetStatus($"选择图片失败：{ex.Message}");
        }
    }

    /// <summary>从剪贴板粘贴图片（Ctrl+V 亦触发本处理器）。</summary>
    private async void OnPasteClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await AddFromClipboardAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "批量扫描粘贴图片失败。");
            SetStatus($"粘贴失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 截屏取图：拉起系统截图工具（Win+Shift+S 同一入口）逐张框选单据区域，
    /// 每完成一张截图会自动落到本窗口的卡片列表。
    /// </summary>
    private async void OnCaptureClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!await Launcher.LaunchUriAsync(new Uri("ms-screenclip:")))
            {
                SetStatus("无法启动系统截图工具，请改用「粘贴图片」或「选择图片」。");
                return;
            }

            SetStatus("已打开系统截图工具，框选完成后图片会自动加入列表；可连续截多张。");

            for (var i = 0; i < CapturePollCount; i++)
            {
                await Task.Delay(CapturePollIntervalMs);

                var content = Clipboard.GetContent();
                if (content.Contains(StandardDataFormats.Bitmap) || content.Contains(StandardDataFormats.StorageItems))
                {
                    await AddFromClipboardAsync();
                    return;
                }
            }

            SetStatus("等待截图超时，请重试或改用「粘贴图片」/「选择图片」。");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "批量扫描截屏失败。");
            SetStatus($"截屏失败：{ex.Message}");
        }
    }

    /// <summary>拖拽经过窗口：仅接受文件类内容并显示提示文案。</summary>
    private void OnRootDragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "松开以导入单据图片";
            e.DragUIOverride.IsCaptionVisible = true;
            e.DragUIOverride.IsContentVisible = true;
        }
    }

    /// <summary>拖拽释放：把拖入的全部图片文件加入列表。</summary>
    private async void OnRootDrop(object sender, DragEventArgs e)
    {
        try
        {
            if (!e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                return;
            }

            var items = await e.DataView.GetStorageItemsAsync();
            var files = items.OfType<StorageFile>().Where(IsImageFile).ToList();
            if (files.Count == 0)
            {
                SetStatus("拖入的内容里没有图片文件。");
                return;
            }

            foreach (var file in files)
            {
                await AddImageFileAsync(file.Path, file.Name);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "批量扫描拖入图片失败。");
            SetStatus($"拖入失败：{ex.Message}");
        }
    }

    // ==================== 卡片管理 ====================

    /// <summary>把一张图片加入卡片列表并排队识别；同一文件不重复加入。</summary>
    /// <param name="path">图片完整路径。</param>
    /// <param name="displayName">卡片标题显示的文件名。</param>
    private async Task AddImageFileAsync(string path, string displayName)
    {
        if (Items.Any(x => string.Equals(x.SourcePath, path, StringComparison.OrdinalIgnoreCase)))
        {
            SetStatus($"{displayName} 已在列表中，跳过重复添加。");
            return;
        }

        var item = new BatchScanItem
        {
            SourcePath = path,
            FileName = displayName,
            Thumbnail = await LoadThumbnailAsync(path)
        };
        Items.Add(item);
        SetStatus($"已加入 {displayName}，当前共 {Items.Count} 张待识别。");
        PumpRecognition();
    }

    /// <summary>从剪贴板取图：位图落盘为临时 PNG，或直接取剪贴板中的图片文件。</summary>
    private async Task AddFromClipboardAsync()
    {
        var content = Clipboard.GetContent();

        if (content.Contains(StandardDataFormats.StorageItems))
        {
            var files = (await content.GetStorageItemsAsync())
                .OfType<StorageFile>()
                .Where(IsImageFile)
                .ToList();
            if (files.Count > 0)
            {
                foreach (var file in files)
                {
                    await AddImageFileAsync(file.Path, file.Name);
                }

                return;
            }
        }

        if (content.Contains(StandardDataFormats.Bitmap))
        {
            var reference = await content.GetBitmapAsync();
            var tempPath = await WriteClipboardBitmapToTempAsync(reference);
            if (tempPath is not null)
            {
                await AddImageFileAsync(tempPath, Path.GetFileName(tempPath));
                return;
            }
        }

        SetStatus("剪贴板里没有可用的图片，请先截图或复制图片后再粘贴。");
    }

    /// <summary>把剪贴板位图重新编码为 PNG 落盘到临时目录，保证后续能复制进数据目录持久保存。</summary>
    /// <param name="reference">剪贴板位图引用。</param>
    /// <returns>临时 PNG 的完整路径；编码失败返回 null。</returns>
    private static async Task<string?> WriteClipboardBitmapToTempAsync(RandomAccessStreamReference reference)
    {
        using var stream = await reference.OpenReadAsync();
        var decoder = await BitmapDecoder.CreateAsync(stream);
        var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);

        using (bitmap)
        {
            var directory = Path.Combine(Path.GetTempPath(), "MaterialLedgerBatch");
            Directory.CreateDirectory(directory);

            var folder = await StorageFolder.GetFolderFromPathAsync(directory);
            var file = await folder.CreateFileAsync(
                $"clipboard_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png",
                CreationCollisionOption.ReplaceExisting);

            using var output = await file.OpenAsync(FileAccessMode.ReadWrite);
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
            encoder.SetSoftwareBitmap(bitmap);
            await encoder.FlushAsync();
            return file.Path;
        }
    }

    /// <summary>加载低分辨率缩略图：多张卡片同时显示也不占过多内存。</summary>
    private static async Task<BitmapImage?> LoadThumbnailAsync(string path)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            using var stream = await file.OpenAsync(FileAccessMode.Read);

            var image = new BitmapImage { DecodePixelWidth = 240 };
            await image.SetSourceAsync(stream);
            return image;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>移除卡片：仅从列表移除，已保存的台账记录与图片不受影响。</summary>
    private void OnRemoveItemClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: BatchScanItem item })
        {
            Items.Remove(item);
            SetStatus($"已移除 {item.FileName}。");
        }
    }

    // ==================== 识别队列 ====================

    /// <summary>批量识别进度快照：Current 从 1 计数；Finished=true 表示整批处理完毕。</summary>
    public sealed record OcrBatchProgress(int Current, int Total, bool Finished);

    /// <summary>
    /// 批量识别进度广播（静态事件，窗口外如台账页订阅后可在右下角悬浮卡片展示进度）。
    /// 订阅方务必在窗口 Closed 后退订，避免持有已销毁窗口的引用。
    /// </summary>
    public static event EventHandler<OcrBatchProgress>? BatchProgressChanged;

    /// <summary>启动识别队列循环：逐张处理待识别的卡片，一次一张，互不并发。</summary>
    private async void PumpRecognition()
    {
        if (_isRecognizing)
        {
            return;
        }

        _isRecognizing = true;
        try
        {
            // 每张卡片处理完成后广播一次进度（第 N/共 M 张），供台账页悬浮卡片展示
            var total = Items.Count(x => x.RecognitionPending);
            var current = 0;
            while (Items.FirstOrDefault(x => x.RecognitionPending) is { } item)
            {
                await RecognizeItemAsync(item);
                current++;
                BatchProgressChanged?.Invoke(this, new OcrBatchProgress(current, total, Finished: false));
            }

            if (total > 0)
            {
                BatchProgressChanged?.Invoke(this, new OcrBatchProgress(total, total, Finished: true));
            }
        }
        finally
        {
            _isRecognizing = false;
        }
    }

    /// <summary>
    /// 识别一张卡片：解码图片 → 按设置引擎识别（在线 AI → 本地 Ollama → 离线 PaddleOCR）→
    /// 全文与字段预填。
    /// </summary>
    /// <param name="item">待识别的卡片。</param>
    private async Task RecognizeItemAsync(BatchScanItem item)
    {
        item.IsBusy = true;
        item.StatusText = "正在识别...";
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(item.SourcePath);
            using var stream = await file.OpenAsync(FileAccessMode.Read);
            var decoded = await OcrImageDecoder.DecodeAsync(stream);

            // 读原始字节给视觉大模型识图用；读不到也能走离线识别，只是跳过 AI 引擎
            byte[]? imageBytes = null;
            try
            {
                imageBytes = await File.ReadAllBytesAsync(item.SourcePath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "读取图片原始字节失败，本次将跳过 AI 引擎：{File}", item.SourcePath);
            }

            var result = await OcrEngineRouter.RecognizeAsync(imageBytes, decoded.Bgra, decoded.Width, decoded.Height);

            // AI 引擎按 JSON 协议返回结构化字段：直接解析并展示 raw 原文；
            // 解析失败（模型没按协议输出）回退正则抽取，离线引擎则始终走正则
            var aiFields = ReceiptFieldExtractor.ParseAiStructured(result.Text);
            var displayText = aiFields is not null
                ? ReceiptFieldExtractor.TryReadAiRawText(result.Text) ?? result.Text
                : result.Text;

            item.RawText = displayText;
            ApplyExtractedFields(item, aiFields ?? ReceiptFieldExtractor.Extract(displayText));
            item.RecognitionPending = false;
            item.StatusText = string.IsNullOrWhiteSpace(displayText)
                ? "未识别到文字，请直接在右侧字段中手动填写后保存。"
                : $"识别完成（{result.EngineName}）。{result.FallbackNote}请核对字段，有误可直接修改后保存。";

            if (result.FallbackNote is not null)
            {
                _logger.LogWarning("批量扫描识别发生引擎回退：{File}，{Note}", item.FileName, result.FallbackNote);
            }
        }
        catch (Exception ex)
        {
            item.RecognitionPending = false;
            item.StatusText = $"识别失败：{ex.Message}。可手动填写字段后保存。";
            _logger.LogError(ex, "批量扫描识别失败：{File}", item.FileName);
        }
        finally
        {
            item.IsBusy = false;
        }
    }

    /// <summary>把正则抽取的字段预填到卡片；「未识别」占位与空值一律跳过。</summary>
    /// <param name="item">目标卡片。</param>
    /// <param name="fields"><see cref="ReceiptFieldExtractor.Extract"/> 的抽取结果。</param>
    private static void ApplyExtractedFields(BatchScanItem item, IReadOnlyList<ReceiptField> fields)
    {
        foreach (var field in fields)
        {
            if (string.IsNullOrWhiteSpace(field.Value)
                || string.Equals(field.Value, ReceiptFieldExtractor.NotFound, StringComparison.Ordinal))
            {
                continue;
            }

            switch (field.Label)
            {
                case "物资名称":
                    item.MaterialName = field.Value;
                    break;
                case "规格":
                    item.Specification = field.Value;
                    break;
                case "数量":
                    item.QuantityText = field.Value;
                    break;
                case "单位":
                    item.Unit = field.Value;
                    break;
                // 单价与单张导入一致：识别率低不自动填，留给合同匹配或手动输入
                case "进场日期":
                    item.EntryDateText = field.Value;
                    break;
                case "单据编号":
                    item.DocNo = field.Value;
                    break;
                case "供应商":
                    item.Supplier = field.Value;
                    break;
                case "经办人":
                    item.Handler = field.Value;
                    break;
                case "车牌号":
                    item.PlateNumber = field.Value;
                    break;
            }
        }
    }

    /// <summary>「按当前文字重提字段」：对手动修正过的识别全文重新跑字段抽取，不重新识别图片。</summary>
    private void OnReExtractClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: BatchScanItem item })
        {
            ApplyExtractedFields(item, ReceiptFieldExtractor.Extract(item.RawText));
            item.StatusText = "已按当前识别文字重新提取字段，请核对后保存。";
        }
    }

    // ==================== 保存入台账 ====================

    /// <summary>「保存入台账」：校验必填 → 组装台账行 → 合同单价匹配 → 图片复制进数据目录 → 落库。</summary>
    private void OnSaveItemClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: BatchScanItem item } || item.IsSaved)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(item.MaterialName))
        {
            item.StatusText = "物资名称为空，未保存。请在右侧手动填写物资名称。";
            return;
        }

        try
        {
            var row = new MaterialLedger
            {
                EntryDate = NormalizeDate(item.EntryDateText),
                EntryTime = DateTime.Now.ToString("HH:mm"),
                DocNo = item.DocNo.Trim(),
                MaterialName = item.MaterialName.Trim(),
                Quantity = ParseDouble(item.QuantityText),
                Unit = item.Unit.Trim(),
                UnitPrice = ParseDouble(item.UnitPriceText),
                Supplier = item.Supplier.Trim(),
                Handler = item.Handler.Trim(),
                PlateNumber = item.PlateNumber.Trim(),
                Specification = item.Specification.Trim()
            };
            row.Amount = Math.Round(row.Quantity * row.UnitPrice, 2);

            // 合同单价匹配：与单张 OCR 导入同一套规则（单价为 0 时用合同价回填，顺带补单位/供应商）
            var contractItem = DatabaseService.Instance.MatchContractPrice(row.Supplier, row.MaterialName);
            var contractHint = string.Empty;
            if (contractItem is not null)
            {
                if (row.UnitPrice == 0)
                {
                    row.UnitPrice = contractItem.UnitPrice;
                    row.Amount = Math.Round(row.Quantity * row.UnitPrice, 2);
                }

                if (!string.IsNullOrWhiteSpace(contractItem.Unit))
                {
                    row.Unit = contractItem.Unit;
                }

                if (string.IsNullOrWhiteSpace(row.Supplier))
                {
                    row.Supplier = contractItem.SupplierName;
                }

                contractHint = $"已匹配合同单价 {contractItem.UnitPrice} 元/{contractItem.Unit}。";
            }

            // 扫描图片复制进数据库同目录的 LedgerImages 持久保存，路径写入台账新列
            row.LedgerImagePath = DatabaseService.Instance.SaveLedgerImage(item.SourcePath);
            DatabaseService.Instance.AddMaterialLedger(row);

            item.IsSaved = true;
            item.StatusText = $"{contractHint}已保存为台账 #{row.Id}。";
            SetStatus($"已保存 {item.FileName} 为台账 #{row.Id}。关闭本窗口后请点击台账页「刷新」查看。");
            _logger.LogInformation(
                "批量扫描保存台账成功，Id={Id}，名称={Name}，图片={Image}。",
                row.Id, row.MaterialName, row.LedgerImagePath);
        }
        catch (Exception ex)
        {
            item.StatusText = $"保存失败：{ex.Message}";
            _logger.LogError(ex, "批量扫描保存台账失败：{File}", item.FileName);
        }
    }

    // ==================== 帮助方法 ====================

    /// <summary>更新窗口底部全局状态提示。</summary>
    private void SetStatus(string message)
    {
        GlobalStatus.Text = message;
    }

    /// <summary>按扩展名判断是否为可识别的图片文件。</summary>
    private static bool IsImageFile(StorageFile file)
    {
        var extension = Path.GetExtension(file.Name).ToLowerInvariant();
        return extension is ".jpg" or ".jpeg" or ".png" or ".bmp" or ".tif" or ".tiff" or ".gif" or ".webp";
    }

    /// <summary>从文本中提取数值，无法解析时返回 0。</summary>
    private static double ParseDouble(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return 0;
        }

        var match = System.Text.RegularExpressions.Regex.Match(value, @"-?\d+(?:\.\d+)?");
        return match.Success && double.TryParse(match.Value, out var result) ? result : 0;
    }

    /// <summary>把日期文本规范成 yyyy-MM-dd；无法解析时回退当天。</summary>
    private static string NormalizeDate(string value)
    {
        return DateTime.TryParse(value, out var parsed)
            ? parsed.ToString("yyyy-MM-dd")
            : DateTime.Now.ToString("yyyy-MM-dd");
    }
}
