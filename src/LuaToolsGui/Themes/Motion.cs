using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace LuaToolsGui.Themes;

/// <summary>
/// GPU-composited transitions and hover helpers (<see cref="UIElement.OpacityProperty"/> and
/// <see cref="TranslateTransform"/> only — never layout properties). Honours
/// <see cref="SystemParameters.ClientAreaAnimation"/> and resets cleanly on recycled containers.
/// </summary>
public static class Motion
{
    public static readonly TimeSpan ModeSwitchDuration = TimeSpan.FromMilliseconds(140);
    public static readonly TimeSpan ViewTransitionDuration = TimeSpan.FromMilliseconds(170);
    public static readonly TimeSpan HoverDuration = TimeSpan.FromMilliseconds(110);

    private static readonly CubicEase EaseOut = CreateFrozenEase();

    private static CubicEase CreateFrozenEase()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        ease.Freeze();
        return ease;
    }

    /// <summary>Testable reduced-motion switch; defaults to Windows client-area animation setting.</summary>
    internal static Func<bool> AnimationsEnabledProvider { get; set; } = static () => SystemParameters.ClientAreaAnimation;

    public static bool AreAnimationsEnabled => AnimationsEnabledProvider();

    /// <summary>
    /// Animate an element entering via GPU-friendly opacity and translation transforms. Under reduced
    /// motion it snaps cleanly to resting state with no pending animation clocks.
    /// </summary>
    public static void AnimateEntrance(
        FrameworkElement? target,
        double fromX = 0,
        double fromY = 0,
        TimeSpan? duration = null)
    {
        if (target is null) return;

        // Null when the element already carries a caller-owned transform: animate opacity only, never clobber it.
        var translate = EnsureTranslateTransform(target);

        if (!AreAnimationsEnabled)
        {
            target.BeginAnimation(UIElement.OpacityProperty, null);
            target.Opacity = 1;
            if (translate is not null)
            {
                translate.BeginAnimation(TranslateTransform.XProperty, null);
                translate.BeginAnimation(TranslateTransform.YProperty, null);
                translate.X = 0;
                translate.Y = 0;
            }
            return;
        }

        var dur = duration ?? ModeSwitchDuration;

        // Resting base values stay at 1 / 0 so FillBehavior.Stop hands control cleanly back at completion.
        target.Opacity = 1;

        target.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, dur)
        {
            EasingFunction = EaseOut,
            FillBehavior = FillBehavior.Stop,
        });

        if (translate is null) return;

        translate.X = 0;
        translate.Y = 0;

        if (fromX != 0)
        {
            translate.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(fromX, 0, dur)
            {
                EasingFunction = EaseOut,
                FillBehavior = FillBehavior.Stop,
            });
        }
        else
        {
            translate.BeginAnimation(TranslateTransform.XProperty, null);
        }

        if (fromY != 0)
        {
            translate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(fromY, 0, dur)
            {
                EasingFunction = EaseOut,
                FillBehavior = FillBehavior.Stop,
            });
        }
        else
        {
            translate.BeginAnimation(TranslateTransform.YProperty, null);
        }
    }

    private static TranslateTransform? EnsureTranslateTransform(FrameworkElement target)
    {
        // Reuse only a resting (0,0) translate — ours from a previous run; a caller's offset is never zeroed.
        if (target.RenderTransform is TranslateTransform { IsFrozen: false } existing)
            return existing.GetAnimationBaseValue(TranslateTransform.XProperty) is 0.0
                && existing.GetAnimationBaseValue(TranslateTransform.YProperty) is 0.0 ? existing : null;
        if (target.RenderTransform is not null && !ReferenceEquals(target.RenderTransform, Transform.Identity))
            return null;

        var created = new TranslateTransform();
        target.RenderTransform = created;
        return created;
    }

    public static readonly DependencyProperty EnableSmoothHoverProperty = DependencyProperty.RegisterAttached(
        "EnableSmoothHover",
        typeof(bool),
        typeof(Motion),
        new PropertyMetadata(false, OnEnableSmoothHoverChanged));

    public static bool GetEnableSmoothHover(DependencyObject obj) => (bool)obj.GetValue(EnableSmoothHoverProperty);
    public static void SetEnableSmoothHover(DependencyObject obj, bool value) => obj.SetValue(EnableSmoothHoverProperty, value);

    private static void OnEnableSmoothHoverChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement host) return;

        host.MouseEnter -= OnHostMouseEnter;
        host.MouseLeave -= OnHostMouseLeave;
        host.DataContextChanged -= OnHostDataContextChanged;
        host.Loaded -= OnHostLoaded;
        host.Unloaded -= OnHostUnloaded;

        if (e.NewValue is true)
        {
            host.MouseEnter += OnHostMouseEnter;
            host.MouseLeave += OnHostMouseLeave;
            host.DataContextChanged += OnHostDataContextChanged;
            host.Loaded += OnHostLoaded;
            host.Unloaded += OnHostUnloaded;
        }
    }

    private static void OnHostMouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (sender is FrameworkElement host && FindHoverOverlay(host) is { } overlay)
            AnimateOpacity(overlay, 1);
    }

    private static void OnHostMouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (sender is FrameworkElement host && FindHoverOverlay(host) is { } overlay)
            AnimateOpacity(overlay, 0);
    }

    private static void OnHostDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is FrameworkElement host)
            ResetSmoothHover(host);
    }

    // Tab switch (Unloaded → Loaded) and container recycling never fire MouseLeave reliably, so the overlay
    // is resynchronised on both edges instead of keeping the last animated target (stuck "lit" card).
    private static void OnHostLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement host)
            ResetSmoothHover(host);
    }

    private static void OnHostUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement host && FindHoverOverlay(host) is { } overlay)
        {
            overlay.BeginAnimation(UIElement.OpacityProperty, null);
            overlay.Opacity = 0;
        }
    }

    /// <summary>Synchronize hover overlay without animation — essential when a virtualized container is recycled.</summary>
    internal static void ResetSmoothHover(FrameworkElement host)
    {
        if (FindHoverOverlay(host) is not { } overlay) return;
        overlay.BeginAnimation(UIElement.OpacityProperty, null);
        overlay.Opacity = host.IsMouseOver ? 1 : 0;
    }

    private static FrameworkElement? FindHoverOverlay(FrameworkElement host) =>
        host.FindName("HoverOverlay") as FrameworkElement;

    private static void AnimateOpacity(FrameworkElement target, double toOpacity)
    {
        if (!AreAnimationsEnabled)
        {
            target.BeginAnimation(UIElement.OpacityProperty, null);
            target.Opacity = toOpacity;
            return;
        }

        double from = target.Opacity;
        target.Opacity = toOpacity;
        target.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(from, toOpacity, HoverDuration)
        {
            EasingFunction = EaseOut,
            FillBehavior = FillBehavior.Stop,
        });
    }
}
