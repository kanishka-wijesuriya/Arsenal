using Arsenal.UI.ViewModels;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Arsenal.UI.Views.Windows
{
    public partial class QuickPanelWindow
    {
        /// <summary>How far back along its entrance the card starts, in DIPs.</summary>
        private const double EnterDistance = 54;

        /// <summary>How far back the card retreats on the way out. Shorter than it came.</summary>
        private const double ExitDistance = 40;

        /// <summary>
        /// Unit vector pointing at the taskbar: the direction the card arrives from and
        /// leaves towards. Set with the placement, so one ease drives whichever axis the
        /// current taskbar edge calls for.
        /// </summary>
        private double _enterX;
        private double _enterY = 1;

        /// <summary>Current distance back along that vector, in DIPs.</summary>
        private double _panelOffset;

        private void SetPanelOffset(double distance)
        {
            _panelOffset = distance;
            PanelTransform.X = _enterX * distance;
            PanelTransform.Y = _enterY * distance;
        }

        public void ShowAnimated()
        {
            _targetVisible = true;
            _shownAt = DateTime.UtcNow;
            _deactivateDismissedAt = DateTime.MinValue;

            _panelFade.Stop();
            _panelSlide.Stop();
            Opacity = 0;

            // Placed and measured at rest, so neither the entrance offset nor a previous
            // exit can be mistaken for the card's own geometry.
            SetPanelOffset(0);
            Show();

            // Read the taskbar first: which edge it is on decides which edge of the
            // window the card is glued to, and that has to be settled before the
            // viewport below is measured and pinned.
            CaptureTrayAnchor();

            // Before the viewport is sized: the sizing below is in DIPs, and they are
            // only worth the right number of pixels once the HWND is standing on the
            // monitor the anchor was read from.
            MoveToAnchorMonitor();

            MainView.ScrollToTop();

            // Re-fit on every open because the monitor or its scale may have changed
            // while the panel was hidden. The resulting viewport remains fixed for the
            // visible session, so transitions cannot make the tray anchor tremble.
            UpdateLayout();
            LockNativeViewport();
            PositionNearTray();

            // Render-only, so it costs no layout and cannot disturb the placement above.
            SetPanelOffset(EnterDistance);
            TakeForeground();

            _panelFade.Start(0, 1, 160, Controls.FrameEase.QuinticOut, v => Opacity = v);
            _panelSlide.Start(EnterDistance, 0, 220, Controls.FrameEase.QuinticOut, SetPanelOffset);
        }

        private readonly Controls.FrameEase _panelFade = new();
        private readonly Controls.FrameEase _panelSlide = new();

        public void HideAnimated(Action? completed = null)
        {
            _targetVisible = false;
            if (!IsVisible)
            {
                completed?.Invoke();
                return;
            }

            double currentOpacity = Opacity;
            double currentOffset = _panelOffset;
            _panelFade.Stop();
            _panelSlide.Stop();
            Opacity = currentOpacity;
            SetPanelOffset(currentOffset);

            // Kept short: the panel is a layered window, so each frame costs a full
            // surface copy and a long exit reads as the window lagging behind the click.
            _panelFade.Start(currentOpacity, 0, 120, Controls.FrameEase.QuarticIn, v => Opacity = v);
            _panelSlide.Start(
                currentOffset,
                ExitDistance,
                140,
                Controls.FrameEase.QuarticIn,
                SetPanelOffset,
                completed: () =>
                {
                    if (!_targetVisible)
                    {
                        Hide();
                        // Leave the detail page and edit mode behind so the panel always
                        // opens on the plain tile grid, and snap the views back while
                        // hidden rather than animating them.
                        if (DataContext is QuickPanelViewModel vm) vm.ReturnToRest();
                        ResetToMainView();
                    }
                    Opacity = 1;
                    SetPanelOffset(0);
                    completed?.Invoke();
                });
        }

        public void HideForModal()
        {
            if (!IsVisible) return;

            var frame = new DispatcherFrame();
            var fallback = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(320) };
            fallback.Tick += (_, _) =>
            {
                fallback.Stop();
                frame.Continue = false;
            };
            HideAnimated(() => frame.Continue = false);
            fallback.Start();
            Dispatcher.PushFrame(frame);
        }
    }
}
