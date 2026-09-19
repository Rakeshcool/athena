// An auto-hiding overlay ScrollBar for the settings page scroller: the bar
// fades in when scrolling starts and fades out a moment after it stops —
// the Fluent design behavior where chrome appears on interaction and
// dissolves when not needed. It never reserves layout space; it draws over
// the content's right edge, so the page width is unchanged either way.
//
// Mechanism: a ScrollViewer-style that finds its own PART_VerticalScrollBar
// once loaded and attaches to it. Thumb drag, Scroll changes, and mouse-
// wheel over the viewer all refresh the timer; 900ms of stillness fades the
// bar to opacity 0 (150ms ease) and collapses IsHitTestVisible so the
// invisible bar can never eat a click near the right edge.

using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Athena.App.Windows;

public sealed class AutoHideScrollBarHelper
{
    private static readonly TimeSpan IdleBeforeFade = TimeSpan.FromMilliseconds(900);
    private const double ShownOpacity = 0.9;
    private const double HiddenOpacity = 0.0;

    public static readonly DependencyProperty EnabledProperty =
        DependencyProperty.RegisterAttached(
            "Enabled", typeof(bool), typeof(AutoHideScrollBarHelper),
            new PropertyMetadata(false, OnEnabledChanged));

    /// <summary>Attach to a ScrollViewer: its vertical bar becomes an
    /// overlay that fades out when idle.</summary>
    public static void SetEnabled(DependencyObject d, bool value) => d.SetValue(EnabledProperty, value);
    public static bool GetEnabled(DependencyObject d) => (bool)d.GetValue(EnabledProperty);

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ScrollViewer viewer) return;
        if ((bool)e.NewValue)
        {
            viewer.Loaded += OnLoaded;
            if (viewer.IsLoaded) Attach(viewer); // already in the tree (re-templated late)
        }
        else
        {
            viewer.Loaded -= OnLoaded;
            if (viewer.GetValue(AttachedProperty) is State old) old.Detach();
            viewer.SetValue(AttachedProperty, null);
        }
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is ScrollViewer viewer) Attach(viewer);
    }

    private static readonly DependencyProperty AttachedProperty =
        DependencyProperty.RegisterAttached(
            "Attached", typeof(State), typeof(AutoHideScrollBarHelper),
            new PropertyMetadata(null));

    private static void Attach(ScrollViewer viewer)
    {
        if (viewer.GetValue(AttachedProperty) is not null) return; // idempotent
        var bar = viewer.Template?.FindName("PART_VerticalScrollBar", viewer) as System.Windows.Controls.Primitives.ScrollBar;
        bar ??= FindDescendant<System.Windows.Controls.Primitives.ScrollBar>(viewer, static s => s.Orientation == Orientation.Vertical);
        if (bar is null) return; // template not realized yet; Loaded covers the rest

        var state = new State(viewer, bar);
        viewer.SetValue(AttachedProperty, state);

        bar.Opacity = HiddenOpacity;
        bar.IsHitTestVisible = false;

        viewer.ScrollChanged += state.OnScroll;
        viewer.MouseEnter += state.OnActivity;
        viewer.MouseLeave += state.OnLeave;
        bar.ValueChanged += state.OnScroll;
        bar.MouseEnter += state.OnActivity;   // aiming at a faded bar shows it
        bar.MouseLeave += state.OnLeave;
        // Template swaps (theme change) would orphan the attach; re-run once.
        viewer.IsVisibleChanged += state.OnVisibleChanged;
    }

    private sealed class State
    {
        private readonly ScrollViewer _viewer;
        private readonly System.Windows.Controls.Primitives.ScrollBar _bar;
        private readonly DispatcherTimer _idle;

        public State(ScrollViewer viewer, System.Windows.Controls.Primitives.ScrollBar bar)
        {
            _viewer = viewer;
            _bar = bar;
            _idle = new DispatcherTimer(IdleBeforeFade, DispatcherPriority.Background,
                (_, _) => FadeTo(HiddenOpacity, hitTest: false), viewer.Dispatcher);
        }

        public void Detach()
        {
            _idle.Stop();
            _viewer.ScrollChanged -= OnScroll;
            _viewer.MouseEnter -= OnActivity;
            _viewer.MouseLeave -= OnLeave;
            _bar.ValueChanged -= OnScroll;
            _bar.MouseEnter -= OnActivity;
            _bar.MouseLeave -= OnLeave;
            _viewer.IsVisibleChanged -= OnVisibleChanged;
        }

        public void OnScroll(object sender, ScrollChangedEventArgs e) => Wake();
        public void OnScroll(object sender, RoutedPropertyChangedEventArgs<double> e) => Wake();
        public void OnActivity(object sender, System.Windows.Input.MouseEventArgs e) => Wake();

        public void OnLeave(object sender, System.Windows.Input.MouseEventArgs e)
        {
            // Leaving with no scroll underway resumes the idle countdown.
            if (!_viewer.ScrollableHeight.Equals(0) && _bar.Value is var _)
                RestartIdle();
        }

        public void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            // Window hidden/shown: reset to hidden so a stale bar never lingers.
            if (!_viewer.IsVisible)
            {
                _idle.Stop();
                _bar.Opacity = HiddenOpacity;
                _bar.IsHitTestVisible = false;
            }
        }

        private void Wake()
        {
            if (_viewer.ScrollableHeight <= 0) return; // nothing to scroll: stay hidden
            _bar.IsHitTestVisible = true;
            FadeTo(ShownOpacity, hitTest: true);
            RestartIdle();
        }

        private void RestartIdle()
        {
            _idle.Stop();
            _idle.Start();
        }

        private void FadeTo(double opacity, bool hitTest)
        {
            _bar.BeginAnimation(System.Windows.UIElement.OpacityProperty,
                new DoubleAnimation(opacity, TimeSpan.FromMilliseconds(150))
                { EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });
            _bar.IsHitTestVisible = hitTest || opacity > 0;
        }
    }

    private static T? FindDescendant<T>(DependencyObject root, Func<T, bool> predicate)
        where T : class
    {
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T match && predicate(match)) return match;
            var nested = FindDescendant(child, predicate);
            if (nested is not null) return nested;
        }
        return null;
    }
}
