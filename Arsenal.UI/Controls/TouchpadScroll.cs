using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using static Arsenal.UI.Controls.DirectManipulationInterop;

namespace Arsenal.UI.Controls
{
    /// <summary>
    /// Scrolls a window's pages from a precision touchpad through DirectManipulation, the
    /// input engine behind scrolling in Explorer, Settings, Edge and WinUI apps.
    /// </summary>
    /// <remarks>
    /// <para>Without it WPF only ever sees a touchpad as a stream of synthesized wheel
    /// messages, and <see cref="SmoothScroll"/> has to infer finger movement from their
    /// sizes and timing. Handing the contact to DirectManipulation returns the finger
    /// position itself, every frame, and the system's own inertia once the fingers lift,
    /// so the page tracks and coasts the way a WinUI page does.</para>
    ///
    /// <para>Only a contact that starts over a <see cref="SmoothScroll"/> page scroller is
    /// taken. Anything else, such as a list or text box with a scroller of its own, or an
    /// open drop-down, keeps the wheel messages it had before. So does the whole window if
    /// DirectManipulation cannot be set up, or when <c>legacy_touchpad_scroll</c> is set
    /// in the config.</para>
    /// </remarks>
    internal sealed class TouchpadScroll
    {
        private const int WmDestroy = 0x0002;
        private const int DmPointerHitTest = 0x0250;

        /// <summary>
        /// The viewport DirectManipulation moves over its content, in pixels.
        /// </summary>
        /// <remarks>
        /// Neither size is the window's. Only the change in the content transform from one
        /// frame to the next is read, so the content is simply made far larger than the
        /// viewport, which then never meets an edge of DirectManipulation's own: the page
        /// has the only edges that count. The viewport is put back at the origin after
        /// each gesture.
        ///
        /// The content reaches as far above and left of the origin as below and right of
        /// it, because DirectManipulation only accepts a zoom to a rectangle at the
        /// content's origin: zooming into the middle of a content laid out from zero is
        /// refused as an invalid argument.
        /// </remarks>
        private const int ViewportSize = 1000;
        private const int ContentReach = 100_000;

        /// <summary>
        /// A contact that has not turned into a pan by now was a tap or a click.
        /// </summary>
        private const double UnclaimedContactMs = 500d;

        private static readonly DependencyProperty InstanceProperty =
            DependencyProperty.RegisterAttached(
                "Instance",
                typeof(TouchpadScroll),
                typeof(TouchpadScroll),
                new PropertyMetadata(null));

        private readonly Window _window;
        private readonly HwndSource _source;
        private readonly IntPtr _hwnd;
        private readonly IDirectManipulationManager _manager;
        private readonly IDirectManipulationUpdateManager _updates;
        private readonly IDirectManipulationViewport _viewport;
        private readonly IDirectManipulationContent _content;
        private readonly ViewportEvents _events;
        private readonly uint _eventsCookie;
        private readonly float[] _transform = new float[6];

        private ScrollViewer? _target;
        private double _position;
        private float _lastY;
        private int _status = StatusReady;
        private bool _recentring;
        private bool _pumping;
        private long _contactTimestamp;
        private bool _detached;

        /// <summary>
        /// Starts taking touchpad pans over the window's page scrollers. Called once the
        /// window has a handle, and again for each new handle the window is given.
        /// </summary>
        public static void Attach(Window window)
        {
            if (AppConfig.Is("legacy_touchpad_scroll")) return;
            if (PresentationSource.FromVisual(window) is not HwndSource source) return;
            if (window.GetValue(InstanceProperty) is TouchpadScroll { _detached: false } existing
                && existing._hwnd == source.Handle)
                return;

            try
            {
                window.SetValue(InstanceProperty, new TouchpadScroll(window, source));
            }
            catch (Exception ex)
            {
                // The wheel path still works, so this is a lesser feel and not a fault.
                Logger.WriteLine("Touchpad scrolling unavailable, using wheel input: " + ex.Message);
            }
        }

        private TouchpadScroll(Window window, HwndSource source)
        {
            _window = window;
            _source = source;
            _hwnd = source.Handle;

            _manager = (IDirectManipulationManager)new DirectManipulationManager();

            Guid iid = typeof(IDirectManipulationUpdateManager).GUID;
            _manager.GetUpdateManager(ref iid, out object updates);
            _updates = (IDirectManipulationUpdateManager)updates;

            iid = typeof(IDirectManipulationViewport).GUID;
            _manager.CreateViewport(IntPtr.Zero, _hwnd, ref iid, out object viewport);
            _viewport = (IDirectManipulationViewport)viewport;

            // Vertical travel only: pages here never scroll sideways, and leaving the
            // other axis out projects a slightly diagonal swipe onto the one that moves.
            _viewport.ActivateConfiguration(
                ConfigurationInteraction | ConfigurationTranslationY | ConfigurationTranslationInertia);

            // WPF draws the result, not DirectManipulation, so the transform is pulled
            // once per WPF frame instead of being composited on its own.
            _viewport.SetViewportOptions(ViewportOptionsManualUpdate);

            _events = new ViewportEvents(this);
            _viewport.AddEventHandler(_hwnd, _events, out _eventsCookie);

            var viewportRect = new NativeRect(0, 0, ViewportSize, ViewportSize);
            _viewport.SetViewportRect(ref viewportRect);

            iid = typeof(IDirectManipulationContent).GUID;
            _viewport.GetPrimaryContent(ref iid, out object content);
            _content = (IDirectManipulationContent)content;
            var contentRect = new NativeRect(-ContentReach, -ContentReach, ContentReach, ContentReach);
            _content.SetContentRect(ref contentRect);

            _manager.Activate(_hwnd);
            _viewport.Enable();
            Recentre();

            _source.AddHook(WndProc);
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == DmPointerHitTest)
                OnPointerHitTest((uint)(wParam.ToInt64() & 0xFFFF));
            else if (msg == WmDestroy)
                Detach();

            return IntPtr.Zero;
        }

        /// <summary>
        /// Decides who gets a contact that has just touched down.
        /// </summary>
        /// <remarks>
        /// A contact left alone here is turned into wheel messages as before, which is how
        /// everything that is not a page scroller keeps working unchanged.
        /// </remarks>
        private void OnPointerHitTest(uint pointerId)
        {
            if (_detached) return;
            if (!GetPointerType(pointerId, out int type) || type != PointerTypeTouchpad) return;
            if (FindPageScroller() is not { } viewer) return;

            try
            {
                SmoothScroll.CancelChase(viewer);
                _target = viewer;
                _position = viewer.VerticalOffset;
                _viewport.SetContact(pointerId);
                _contactTimestamp = Stopwatch.GetTimestamp();
                StartPumping();
            }
            catch (Exception ex)
            {
                _target = null;
                Logger.WriteLine("Touchpad contact: " + ex.Message);
            }
        }

        /// <summary>
        /// The page scroller a wheel turned now would reach, if any.
        /// </summary>
        /// <remarks>
        /// Walks out from what is under the pointer, as wheel routing does. A scroller of
        /// some other kind that could itself move on the way out keeps the gesture: that
        /// is a list or a text box, and the wheel messages it would get are its own.
        /// </remarks>
        private ScrollViewer? FindPageScroller()
        {
            if (SmoothScroll.IsPopupCapturingInput()) return null;
            if (Mouse.DirectlyOver is not DependencyObject over) return null;
            if (!ReferenceEquals(PresentationSource.FromDependencyObject(over), _source)) return null;

            for (DependencyObject? node = over; node != null; node = ParentOf(node))
            {
                if (node is not ScrollViewer viewer) continue;

                bool canMove = viewer.ScrollableHeight > 0d
                    && viewer.VerticalScrollBarVisibility != ScrollBarVisibility.Disabled;

                if (SmoothScroll.GetIsEnabled(viewer) && !viewer.CanContentScroll)
                {
                    if (canMove) return viewer;
                    continue;
                }

                if (canMove) return null;
            }

            return null;
        }

        private static DependencyObject? ParentOf(DependencyObject node)
            => node is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);

        private void StartPumping()
        {
            if (_pumping) return;
            _pumping = true;
            CompositionTarget.Rendering += OnRendering;
        }

        private void StopPumping()
        {
            if (!_pumping) return;
            _pumping = false;
            CompositionTarget.Rendering -= OnRendering;
        }

        /// <summary>
        /// Asks DirectManipulation for this frame's position, just before WPF lays out
        /// and draws it, so the offset written here is on screen in the same frame.
        /// </summary>
        private void OnRendering(object? sender, EventArgs args)
        {
            if (_detached)
            {
                StopPumping();
                return;
            }

            bool moving = _status is StatusRunning or StatusInertia;
            double sinceContactMs = (Stopwatch.GetTimestamp() - _contactTimestamp) * 1000d / Stopwatch.Frequency;
            if (!moving && sinceContactMs > UnclaimedContactMs)
            {
                StopPumping();
                _target = null;
                return;
            }

            try
            {
                _updates.Update(IntPtr.Zero);
            }
            catch (Exception ex)
            {
                StopPumping();
                Logger.WriteLine("Touchpad update: " + ex.Message);
            }
        }

        private void OnStatusChanged(int current, int previous)
        {
            _status = current;
            if (current == previous || current != StatusReady) return;

            // The gesture and its inertia are over. The reset is posted rather than made
            // here because it calls back into DirectManipulation, which is mid-callback.
            StopPumping();
            _target = null;
            _window.Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(Recentre));
        }

        private void OnContentUpdated()
        {
            _content.GetContentTransform(_transform, 6);
            float y = _transform[5];
            float delta = y - _lastY;
            _lastY = y;

            if (_recentring || delta == 0f || _target is not { } viewer) return;

            // A jump this size is the reset arriving late, not a finger.
            if (Math.Abs(delta) > ContentReach / 2f) return;

            if (!viewer.IsLoaded || !viewer.IsVisible)
            {
                _target = null;
                return;
            }

            // The transform is in device pixels and follows the fingers, so content moving
            // up is the page scrolling down.
            double scale = VisualTreeHelper.GetDpi(viewer).DpiScaleY;
            _position = Math.Clamp(_position - (delta / scale), 0d, viewer.ScrollableHeight);
            viewer.ScrollToVerticalOffset(_position);
        }

        /// <summary>
        /// Puts the viewport back at the content's origin and takes the transform
        /// there as the point the next gesture is measured from.
        /// </summary>
        private void Recentre()
        {
            if (_detached || _status is StatusRunning or StatusInertia) return;

            _recentring = true;
            try
            {
                _viewport.ZoomToRect(0f, 0f, ViewportSize, ViewportSize, 0);
                _updates.Update(IntPtr.Zero);
                _content.GetContentTransform(_transform, 6);
                _lastY = _transform[5];
            }
            catch (Exception ex)
            {
                Logger.WriteLine("Touchpad recentre: " + ex.Message);
            }
            finally
            {
                _recentring = false;
            }
        }

        private void Detach()
        {
            if (_detached) return;
            _detached = true;
            StopPumping();
            _target = null;
            _source.RemoveHook(WndProc);

            try
            {
                _viewport.RemoveEventHandler(_eventsCookie);
                _viewport.Abandon();
                _manager.Deactivate(_hwnd);
            }
            catch (Exception ex)
            {
                Logger.WriteLine("Touchpad detach: " + ex.Message);
            }

            Marshal.ReleaseComObject(_content);
            Marshal.ReleaseComObject(_viewport);
            Marshal.ReleaseComObject(_updates);
            Marshal.ReleaseComObject(_manager);
            _window.ClearValue(InstanceProperty);
        }

        /// <summary>
        /// The callback object DirectManipulation holds. Kept apart so the scroller itself
        /// is not exposed to COM.
        /// </summary>
        [ComVisible(true)]
        [ClassInterface(ClassInterfaceType.None)]
        private sealed class ViewportEvents : IDirectManipulationViewportEventHandler
        {
            private readonly TouchpadScroll _owner;

            public ViewportEvents(TouchpadScroll owner) => _owner = owner;

            public void OnViewportStatusChanged(IntPtr viewport, int current, int previous)
                => _owner.OnStatusChanged(current, previous);

            public void OnViewportUpdated(IntPtr viewport)
            {
            }

            public void OnContentUpdated(IntPtr viewport, IntPtr content)
                => _owner.OnContentUpdated();
        }
    }
}
