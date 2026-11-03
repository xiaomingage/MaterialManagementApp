using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using MaterialManagementApp.Services;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.System;

namespace MaterialManagementApp.Pages;

/// <summary>
/// 「导入单据 / OCR 识别」非模态窗口（台账明细页 → 导入单据 / OCR 识别）。
/// 上半部分支持图片预览、拖拽、粘贴与截屏取图；下半部分显示正则抽取的字段、识别全文与进度，
/// 识别由内嵌的离线 PaddleOCR 引擎在后台线程完成，全程不联网。
/// </summary>
public sealed partial class OcrImportWindow : Window
{
    /// <summary>等待系统截图写回剪贴板的轮询间隔（毫秒）。</summary>
    private const int CapturePollIntervalMs = 500;

    /// <summary>等待系统截图写回剪贴板的最大轮询次数，约 30 秒。</summary>
    private const int CapturePollCount = 60;

    /// <summary>分类日志实例。</summary>
    private readonly ILogger _logger = AppLog.CreateLogger<OcrImportWindow>();

    /// <summary>当前图片解码后的 BGRA32 像素数据；为 null 表示尚未载入图片。</summary>
    private byte[]? _imageBgra;

    /// <summary>当前图片的原始文件字节，供在线 AI / 本地 Ollama 视觉模型识别；为 null 时只能走离线引擎。</summary>
    private byte[]? _imageBytes;

    /// <summary>当前图片宽度（像素）。</summary>
    private int _imageWidth;

    /// <summary>当前图片高度（像素）。</summary>
    private int _imageHeight;

    /// <summary>是否正在识别中，用于阻止重复触发。</summary>
    private bool _isBusy;

    /// <summary>正则抽取出的字段集合，绑定到窗口内的字段列表。</summary>
    public ObservableCollection<ReceiptField> Fields { get; } = new();

    /// <summary>最近一次识别到的字段结果，供「填入表格」按钮点击时取数。</summary>
    private IReadOnlyList<ReceiptField>? _lastFields;

    /// <summary>
    /// 点击「填入表格」按钮时触发，参数为最近一次识别抽取出的字段。
    /// 业务页面（物资台账、渣土运输等）订阅此事件，把字段按名称映射到新增行。
    /// 仅当识别成功且抽取出至少一个字段时触发。
    /// </summary>
    public event EventHandler<IReadOnlyList<ReceiptField>>? FillRequested;

    /// <summary>初始化窗口并设定合适的初始尺寸。</summary>
    public OcrImportWindow()
    {
        InitializeComponent();

        // Window 不是 FrameworkElement，XAML 里无法用 x:Bind 绑定自身属性，这里在代码里直接挂数据源
        FieldList.ItemsSource = Fields;

        // 上下两栏都需要空间：预览区约 3/5，结果区约 2/5
        AppWindow.Resize(new SizeInt32(1040, 780));
    }

    /// <summary>拖拽经过投放区：仅接受图片类内容，并显示提示文案。</summary>
    /// <param name="sender">投放区 Border。</param>
    /// <param name="e">拖拽事件参数。</param>
    private void OnDropAreaDragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems)
            || e.DataView.Contains(StandardDataFormats.Bitmap))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "松开以导入单据图片";
            e.DragUIOverride.IsCaptionVisible = true;
            e.DragUIOverride.IsContentVisible = true;
        }
    }

    /// <summary>拖拽释放：从拖入的数据包里取出图片并开始识别。</summary>
    /// <param name="sender">投放区 Border。</param>
    /// <param name="e">拖拽事件参数。</param>
    private async void OnDropAreaDrop(object sender, DragEventArgs e)
    {
        try
        {
            await LoadFromDataViewAsync(e.DataView, "拖入的文件");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "拖拽导入单据图片失败。");
            SetStatus($"导入失败：{ex.Message}");
        }
    }

    /// <summary>选择本地单据图片文件。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">路由事件参数。</param>
    private async void OnPickImageClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add(".jpg");
            picker.FileTypeFilter.Add(".jpeg");
            picker.FileTypeFilter.Add(".png");
            picker.FileTypeFilter.Add(".bmp");
            picker.FileTypeFilter.Add(".tif");
            picker.FileTypeFilter.Add(".tiff");

            // WinUI 3 的文件选择器是 Win32 对话框，必须先绑定本窗口句柄，否则构造后直接抛异常
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));

            var file = await picker.PickSingleFileAsync();
            if (file is null)
            {
                return;
            }

            using var stream = await file.OpenAsync(FileAccessMode.Read);
            await LoadImageAsync(stream, file.Name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "选择单据图片失败。");
            SetStatus($"选择图片失败：{ex.Message}");
        }
    }

    /// <summary>从剪贴板粘贴单据图片（Ctrl+V 亦触发本处理器）。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">路由事件参数。</param>
    private async void OnPasteClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await LoadFromDataViewAsync(Clipboard.GetContent(), "剪贴板");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "粘贴单据图片失败。");
            SetStatus($"粘贴失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 截屏取图：拉起系统截图工具（Win+Shift+S 同一入口）框选单据区域，
    /// 截图完成后图片会写入剪贴板，这里轮询等待并自动载入识别。
    /// </summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">路由事件参数。</param>
    private async void OnCaptureClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!await Launcher.LaunchUriAsync(new Uri("ms-screenclip:")))
            {
                SetStatus("无法启动系统截图工具，请改用「粘贴」或「选择图片」。");
                return;
            }

            SetStatus("已打开系统截图工具，请框选单据区域，截图完成后会自动载入识别。");

            for (var i = 0; i < CapturePollCount; i++)
            {
                await Task.Delay(CapturePollIntervalMs);

                var content = Clipboard.GetContent();
                if (content.Contains(StandardDataFormats.Bitmap) || content.Contains(StandardDataFormats.StorageItems))
                {
                    await LoadFromDataViewAsync(content, "截屏");
                    return;
                }
            }

            SetStatus("等待截图超时，请重试或改用「粘贴」/「选择图片」。");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "截屏导入单据失败。");
            SetStatus($"截屏失败：{ex.Message}");
        }
    }

    /// <summary>清空图片、识别结果与进度。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">路由事件参数。</param>
    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        _imageBgra = null;
        _imageBytes = null;
        _imageWidth = 0;
        _imageHeight = 0;

        PreviewImage.Source = null;
        PreviewHint.Visibility = Visibility.Visible;
        RawTextBox.Text = string.Empty;
        Fields.Clear();
        _lastFields = null;
        FillButton.IsEnabled = false;

        OcrProgress.IsIndeterminate = false;
        OcrProgress.Value = 0;
        SetStatus("已清空，请重新导入单据图片。");
    }

    /// <summary>把识别全文复制到剪贴板，便于粘贴到台账或其它表单。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">路由事件参数。</param>
    private void OnCopyAllClick(object sender, RoutedEventArgs e)
    {
        var text = RawTextBox.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            SetStatus("当前没有可复制的识别结果。");
            return;
        }

        var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        package.SetText(text);
        Clipboard.SetContent(package);

        SetStatus("已复制全部识别文字到剪贴板。");
    }

    /// <summary>把识别字段填入到打开本窗口的业务页面（物资台账 / 渣土运输）。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">路由事件参数。</param>
    private void OnFillClick(object sender, RoutedEventArgs e)
    {
        if (_lastFields is null || _lastFields.Count == 0)
        {
            SetStatus("没有可填入的识别结果，请先识别单据图片。");
            return;
        }

        if (FillRequested is null)
        {
            SetStatus("没有业务页面订阅填入事件，请先在表格页面点击「导入单据」。");
            return;
        }

        FillRequested.Invoke(this, _lastFields);
        SetStatus("字段已发送到表格页面，请切回该窗口核对并保存。");
    }

    /// <summary>从数据包（拖拽、剪贴板）中取出图片流并载入。</summary>
    /// <param name="view">数据包视图。</param>
    /// <param name="sourceName">来源说明，仅用于状态提示。</param>
    /// <returns>载入与识别完成的异步任务。</returns>
    private async Task LoadFromDataViewAsync(DataPackageView view, string sourceName)
    {
        if (view.Contains(StandardDataFormats.Bitmap))
        {
            var reference = await view.GetBitmapAsync();
            using var bitmapStream = await reference.OpenReadAsync();
            await LoadImageAsync(bitmapStream, sourceName);
            return;
        }

        if (view.Contains(StandardDataFormats.StorageItems))
        {
            var items = await view.GetStorageItemsAsync();
            var file = items.OfType<StorageFile>().FirstOrDefault(item => IsImageFile(item.Name));
            if (file is not null)
            {
                using var fileStream = await file.OpenAsync(FileAccessMode.Read);
                await LoadImageAsync(fileStream, file.Name);
                return;
            }
        }

        SetStatus($"{sourceName}里没有可用的图片，请改用「选择图片」或「截屏」。");
    }

    /// <summary>
    /// 载入图片：先刷新预览，再把图片解码成 BGRA32 像素交给离线引擎识别。
    /// </summary>
    /// <param name="stream">图片流。</param>
    /// <param name="sourceName">来源说明，仅用于状态提示。</param>
    /// <returns>载入与识别完成的异步任务。</returns>
    private async Task LoadImageAsync(IRandomAccessStream stream, string sourceName)
    {
        // 预览与解码各用一份独立流：BitmapImage 会独占传入的流，解码器必须用克隆流
        using (var previewStream = stream.CloneStream())
        {
            var image = new BitmapImage();
            await image.SetSourceAsync(previewStream);

            PreviewImage.Source = image;
            PreviewHint.Visibility = Visibility.Collapsed;
        }

        stream.Seek(0);
        var decoded = await OcrImageDecoder.DecodeAsync(stream);
        _imageBgra = decoded.Bgra;
        _imageWidth = decoded.Width;
        _imageHeight = decoded.Height;

        // 读一份原始字节给视觉大模型（在线 AI / Ollama）直接识图用；读不到也不影响离线识别
        try
        {
            _imageBytes = await ReadAllBytesAsync(stream);
        }
        catch (Exception ex)
        {
            _imageBytes = null;
            _logger.LogWarning(ex, "读取图片原始字节失败，本次将跳过 AI 引擎直接离线识别。");
        }

        SetStatus($"已载入图片：{sourceName}（{_imageWidth} × {_imageHeight}）。");
        await RecognizeAsync();
    }

    /// <summary>把随机访问流整体读为字节数组；读完后流会被回卷到起点供后续解码复用。</summary>
    /// <param name="stream">图片来源流。</param>
    /// <returns>图片完整字节。</returns>
    private static async Task<byte[]> ReadAllBytesAsync(IRandomAccessStream stream)
    {
        stream.Seek(0);
        var reader = new DataReader(stream);
        try
        {
            await reader.LoadAsync((uint)stream.Size);
            var bytes = new byte[(int)stream.Size];
            reader.ReadBytes(bytes);
            return bytes;
        }
        finally
        {
            // 解除与底层流的关联再释放，避免连带关闭调用方后续还要使用的流
            reader.DetachStream();
            reader.Dispose();
            stream.Seek(0);
        }
    }

    /// <summary>
    /// 按设置页选择的引擎识别当前图片（在线 AI → 本地 Ollama → 离线 PaddleOCR 自动路由与回退），
    /// 并刷新字段与全文。
    /// </summary>
    /// <returns>识别完成的异步任务。</returns>
    private async Task RecognizeAsync()
    {
        if (_imageBgra is null || _isBusy)
        {
            return;
        }

        _isBusy = true;
        SetBusy(true);

        try
        {
            SetStatus("正在识别单据文字...");

            var result = await OcrEngineRouter.RecognizeAsync(_imageBytes, _imageBgra, _imageWidth, _imageHeight);

            // AI 引擎按 JSON 协议返回结构化字段：直接解析并展示 raw 原文；
            // 解析失败（模型没按协议输出）回退正则抽取，离线引擎则始终走正则
            var aiFields = ReceiptFieldExtractor.ParseAiStructured(result.Text);
            var displayText = aiFields is not null
                ? ReceiptFieldExtractor.TryReadAiRawText(result.Text) ?? result.Text
                : result.Text;

            RawTextBox.Text = displayText;
            var fields = aiFields ?? ReceiptFieldExtractor.Extract(displayText);
            UpdateFields(fields);

            if (string.IsNullOrWhiteSpace(displayText))
            {
                OcrProgress.IsIndeterminate = false;
                OcrProgress.Value = 0;
                SetStatus("未从图片中识别到文字，请确认图片清晰度后重试。");
                _logger.LogWarning("OCR 未识别到任何文字，引擎={Engine}。", result.EngineName);
                FillButton.IsEnabled = false;
            }
            else
            {
                OcrProgress.IsIndeterminate = false;
                OcrProgress.Value = 100;
                _lastFields = fields;
                FillButton.IsEnabled = true;
                SetStatus($"识别完成（{result.EngineName}）。{result.FallbackNote}请核对下方抽取字段，确认无误后点击「填入表格」。");
            }
        }
        catch (Exception ex)
        {
            // 模型缺失、图片损坏、内存不足等都会走到这里，窗口必须给出可见提示而不能崩溃
            OcrProgress.IsIndeterminate = false;
            OcrProgress.Value = 0;
            SetStatus($"识别失败：{ex.Message}");
            _logger.LogError(ex, "单据识别失败。");
        }
        finally
        {
            _isBusy = false;
            SetBusy(false);
        }
    }

    /// <summary>用正则抽取出的字段刷新字段列表。</summary>
    /// <param name="fields">从识别全文中抽取出的字段。</param>
    private void UpdateFields(IEnumerable<ReceiptField> fields)
    {
        Fields.Clear();

        foreach (var field in fields)
        {
            Fields.Add(field);
        }
    }

    /// <summary>切换识别中的忙碌状态：进度条改为不确定模式，并禁用取图按钮避免重复触发。</summary>
    /// <param name="busy">是否正在识别。</param>
    private void SetBusy(bool busy)
    {
        PickImageButton.IsEnabled = !busy;
        PasteButton.IsEnabled = !busy;
        CaptureButton.IsEnabled = !busy;

        OcrProgress.IsIndeterminate = busy;
        if (busy)
        {
            OcrProgress.Value = 0;
        }
    }

    /// <summary>更新状态提示文本。</summary>
    /// <param name="message">提示内容。</param>
    private void SetStatus(string message)
    {
        StatusText.Text = message;
    }

    /// <summary>按扩展名判断是否为可识别的图片文件。</summary>
    /// <param name="fileName">文件名。</param>
    /// <returns>是图片返回 true。</returns>
    private static bool IsImageFile(string fileName)
    {
        var extension = System.IO.Path.GetExtension(fileName).ToLowerInvariant();
        return extension is ".jpg" or ".jpeg" or ".png" or ".bmp" or ".tif" or ".tiff" or ".gif" or ".webp";
    }
}
