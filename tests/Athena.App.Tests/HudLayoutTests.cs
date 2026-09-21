// Regression tests for the HUD pill's dual-source layout. Guards the two
// defects found after the text-above/animation-below restructure:
//   1. flow lanes that were never added to the flow grid rendered at zero
//      height — live text invisible in every state (orphaned children);
//   2. the waveform lived in an Auto-height row, so the breathing bars
//      resized the row — and the whole pill — on every animation frame.
// Pure WPF: runs the pill on a dedicated STA thread; no audio or servers.

using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Xunit;
using Athena.App.Hud;

namespace Athena.App.Tests;

public class HudLayoutTests
{
    private sealed class StaDriver : IDisposable
    {
        private readonly Thread _thread;
        private Dispatcher _dispatcher = null!;
        public HudPillWindow Hud = null!;

        public StaDriver()
        {
            Exception? init = null;
            var ready = new ManualResetEventSlim(false);
            _thread = new Thread(() =>
            {
                try
                {
                    Hud = new HudPillWindow();
                    Hud.Show();
                    _dispatcher = Dispatcher.CurrentDispatcher;
                }
                catch (Exception ex) { init = ex; }
                finally { ready.Set(); }
                Dispatcher.Run();
            });
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.IsBackground = true;
            _thread.Start();
            Assert.True(ready.Wait(TimeSpan.FromSeconds(10)), "STA thread failed to start");
            if (init is not null) throw init;
        }

        public void Invoke(Action action) =>
            _dispatcher.Invoke(action, DispatcherPriority.Normal);

        public void Settle() =>
            _dispatcher.Invoke(() => { }, DispatcherPriority.Loaded);

        public void Dispose()
        {
            try { _dispatcher.InvokeShutdown(); } catch { }
            _thread.Join(TimeSpan.FromSeconds(3));
        }
    }

    [Fact]
    public void Dual_source_partials_render_side_by_side_and_visible()
    {
        using var d = new StaDriver();
        d.Invoke(() =>
        {
            d.Hud.ShowPill();
            d.Hud.SetLiveText("microphone words land here");
            d.Hud.SetSystemLiveText("system audio words land here");
        });
        d.Settle();

        d.Invoke(() =>
        {
            var mic = GetPanel(d.Hud, "_flowPanel");
            var sys = GetPanel(d.Hud, "_flowPanelSystem");
            var micText = (TextBlock)GetField(d.Hud, "_flowText");

            Assert.Equal(Visibility.Visible, micText.IsVisible ? Visibility.Visible : Visibility.Collapsed);
            Assert.True(micText.IsVisible, "mic partial text must be on screen");
            Assert.True(mic.ActualHeight > 0, "mic lane must have height (orphaned-lane regression)");
            Assert.True(sys.ActualHeight > 0, "system lane must have height");

            // Side by side, not stacked: the system lane's right edge must sit
            // left of the mic lane's left edge.
            var sysRight = sys.PointToScreen(new Point(sys.ActualWidth, 0)).X;
            var micLeft = mic.PointToScreen(new Point(0, 0)).X;
            Assert.True(sysRight <= micLeft,
                $"lanes overlap: system right {sysRight} vs mic left {micLeft}");
        });
    }

    [Fact]
    public void Pill_height_is_steady_while_bars_animate()
    {
        using var d = new StaDriver();
        d.Invoke(() =>
        {
            d.Hud.ShowPill();
            d.Hud.SetLiveText("steady while the wave dances");
            d.Hud.OnLevel(0.8f);
        });
        d.Settle();

        var heights = new List<double>();
        for (var i = 0; i < 12; i++)
        {
            d.Invoke(() =>
            {
                d.Hud.OnLevel(0.3f + 0.5f * (float)Math.Sin(i));
                heights.Add(((Border)GetField(d.Hud, "_pillBorder")).ActualHeight);
            });
            Thread.Sleep(40);
        }
        d.Settle();

        Assert.Equal(heights[0], heights[8], 0); // no resize across an animation stretch
    }

    private static object GetField(object seed, string name) =>
        seed.GetType().GetField(name, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(seed)!;

    private static Panel GetPanel(HudPillWindow hud, string name) => (Panel)GetField(hud, name);
}
