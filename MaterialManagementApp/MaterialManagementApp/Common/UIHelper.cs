using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;

namespace MaterialManagementApp.Common;

/// <summary>
/// 提供可在 Style 中使用的 UI 辅助附加属性。
/// WinUI 3 的 <see cref="UIElement.Translation"/> 不是依赖属性，无法在 Style Setter 中直接赋值；
/// 本类通过附加属性间接设置 <c>Translation.Z</c>，配合 <see cref="Microsoft.UI.Composition.ThemeShadow"/> 让卡片阴影可见。
/// 同时提供 <see cref="EnableHoverProperty"/> 为卡片注入鼠标悬停时的缩放与阴影抬升反馈。
/// </summary>
public static class UIHelper
{
    /// <summary>Z 轴 elevation（像素），控制 ThemeShadow 阴影深度。</summary>
    public static readonly DependencyProperty ElevationProperty =
        DependencyProperty.RegisterAttached(
            "Elevation",
            typeof(double),
            typeof(UIHelper),
            new PropertyMetadata(0d, OnElevationChanged));

    /// <summary>是否启用悬停动效（缩放 + 阴影抬升）。</summary>
    public static readonly DependencyProperty EnableHoverProperty =
        DependencyProperty.RegisterAttached(
            "EnableHover",
            typeof(bool),
            typeof(UIHelper),
            new PropertyMetadata(false, OnEnableHoverChanged));

    public static double GetElevation(DependencyObject obj) => (double)obj.GetValue(ElevationProperty);

    public static void SetElevation(DependencyObject obj, double value) => obj.SetValue(ElevationProperty, value);

    public static bool GetEnableHover(DependencyObject obj) => (bool)obj.GetValue(EnableHoverProperty);

    public static void SetEnableHover(DependencyObject obj, bool value) => obj.SetValue(EnableHoverProperty, value);

    private static void OnElevationChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is UIElement element && e.NewValue is double z)
        {
            element.Translation = new System.Numerics.Vector3(0, 0, (float)z);
        }
    }

    private static void OnEnableHoverChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element)
        {
            return;
        }

        if (e.NewValue is true)
        {
            element.PointerEntered -= OnPointerEntered;
            element.PointerEntered += OnPointerEntered;
            element.PointerExited -= OnPointerExited;
            element.PointerExited += OnPointerExited;
        }
        else
        {
            element.PointerEntered -= OnPointerEntered;
            element.PointerExited -= OnPointerExited;
        }
    }

    private static void OnPointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (sender is UIElement element)
        {
            AnimateScale(element, 1.025f);
            SetElevation(element, 12);
        }
    }

    private static void OnPointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (sender is UIElement element)
        {
            AnimateScale(element, 1.0f);
            SetElevation(element, 4);
        }
    }

    /// <summary>用 Composition 动画平滑缩放元素，时长 150ms，自定义 ease-out 缓动曲线。</summary>
    private static void AnimateScale(UIElement element, float targetScale)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        var compositor = visual.Compositor;

        // 缩放原点必须显式设为元素中心：Visual.CenterPoint 默认为 (0,0)，
        // 不设置时会绕左上角缩放，视觉上只是轻微位移而非整体放大
        var size = element.RenderSize;
        visual.CenterPoint = new System.Numerics.Vector3((float)size.Width / 2f, (float)size.Height / 2f, 0f);

        // 自定义缓动曲线：控制点 (0.0, 0.0) / (0.2, 1) 等价于标准 ease-out，
        // 起步响应快、收尾平缓，悬停抬升与回落都显得柔和不突兀
        var easing = compositor.CreateCubicBezierEasingFunction(
            new System.Numerics.Vector2(0.0f, 0.0f),
            new System.Numerics.Vector2(0.2f, 1f));

        var animation = compositor.CreateScalarKeyFrameAnimation();
        animation.InsertKeyFrame(1.0f, targetScale, easing);
        animation.Duration = TimeSpan.FromMilliseconds(150);

        visual.StartAnimation("Scale.X", animation);
        visual.StartAnimation("Scale.Y", animation);
    }
}
