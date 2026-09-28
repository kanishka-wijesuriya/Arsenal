using Arsenal.UI.ViewModels;
using System.Windows;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Arsenal.UI.Views.Windows
{
    public partial class QuickPanelWindow
    {
        /// <summary>
        /// Closes the panel because it lost the foreground - clicked away from, tapped
        /// away from, Alt+Tabbed away from, or the Start menu opened over it.
        /// </summary>
        /// <remarks>
        /// The tray-press guard lives here and nowhere else. A tray click deactivates
        /// this window before the notify icon reports the press, and it is the click
        /// itself that must close the panel so the toggle does not reopen it.
        /// </remarks>
        private void DismissForActivationLoss()
        {
            if (System.Windows.Application.Current is App app && app.IsTrayLeftPressActive) return;
            Dismiss();
        }

        /// <summary>
        /// Closes the panel because a click landed somewhere that is provably not on it.
        /// </summary>
        /// <remarks>
        /// Deliberately unguarded. The tray-press guard above once applied to this path
        /// too and swallowed genuine outside clicks, which is what made the panel need
        /// clicking twice. It is not needed here: a click on the tray icon dismisses the
        /// panel through this path, and the tray toggle then claims that dismissal
        /// instead of reopening.
        /// </remarks>
        private void DismissForOutsideClick() => Dismiss();

        /// <summary>
        /// Takes the foreground for real, rather than asking for it.
        /// </summary>
        /// <remarks>
        /// Window.Activate() calls SetForegroundWindow, which Windows refuses unless the
        /// caller already owns the foreground - and when the panel opens from the tray,
        /// the shell owns it. A refused activation leaves the panel visible but never
        /// active, with no focus to lose when the user clicks elsewhere, which killed
        /// every activation-based dismissal signal at once.
        ///
        /// Attaching to the foreground thread's input queue puts this window in the same
        /// input context for the duration of the call, which is what makes the handover
        /// succeed. Once the panel genuinely holds the foreground, Deactivated fires for
        /// every way there is to turn away from it - mouse, touch, taskbar, Alt+Tab, the
        /// Start menu - because all of them take the foreground off us.
        /// </remarks>
        private void TakeForeground()
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;

            IntPtr foreground = GetForegroundWindow();
            if (foreground == hwnd) return;

            uint thisThread = GetCurrentThreadId();
            uint owningThread = foreground == IntPtr.Zero
                ? 0
                : GetWindowThreadProcessId(foreground, out _);

            bool attached = owningThread != 0
                && owningThread != thisThread
                && AttachThreadInput(thisThread, owningThread, true);

            try
            {
                SetForegroundWindow(hwnd);
                SetActiveWindow(hwnd);
                SetFocus(hwnd);
            }
            finally
            {
                if (attached) AttachThreadInput(thisThread, owningThread, false);
            }
        }

        private IntPtr _foregroundHook;
        private WinEventProc? _foregroundHookProc;

        private void HookForegroundChanges()
        {
            if (_foregroundHook != IntPtr.Zero) return;

            // Held in a field for the life of the hook: the callback is invoked from
            // unmanaged code, which does not keep the delegate alive on its own.
            _foregroundHookProc ??= OnForegroundChanged;
            _foregroundHook = SetWinEventHook(
                EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero,
                _foregroundHookProc, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);

            // A hook that silently fails to install takes one of the dismissal paths
            // with it and leaves no trace. Say so once, here, rather than leaving it to
            // be inferred from the panel refusing to close.
            if (_foregroundHook == IntPtr.Zero) Logger.WriteLine("Quick panel: foreground hook not installed");
        }

        private void UnhookForegroundChanges()
        {
            if (_foregroundHook == IntPtr.Zero) return;
            UnhookWinEvent(_foregroundHook);
            _foregroundHook = IntPtr.Zero;
        }

        /// <summary>
        /// Out-of-context hooks are delivered through the hooking thread's message queue,
        /// so this already arrives on the UI thread. Own-process events are filtered by
        /// the hook itself, leaving only a genuine hand-off to another application.
        /// </summary>
        private void OnForegroundChanged(
            IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
        {
            if (idObject != ObjIdWindow) return;
            DismissForActivationLoss();
        }

        private IntPtr _mouseHook;
        private LowLevelMouseProc? _mouseHookProc;

        /// <summary>
        /// Watches for a click anywhere outside the card, whether or not it moves the
        /// foreground.
        /// </summary>
        /// <remarks>
        /// Deactivated and the foreground watch both react to the <em>consequence</em> of
        /// a click, and the taskbar does not produce that consequence: clicking empty
        /// taskbar space, the clock or the notification area deliberately leaves the
        /// foreground where it was, so neither fires and the panel stayed open. Reacting
        /// to the click itself covers those, and the two activation watches stay for what
        /// a mouse hook cannot see - Alt+Tab, and focus moved from the keyboard.
        /// </remarks>
        private void HookOutsideClicks()
        {
            if (_mouseHook != IntPtr.Zero) return;

            _mouseHookProc ??= OnGlobalMouse;
            _mouseHook = SetWindowsHookEx(WhMouseLowLevel, _mouseHookProc, GetModuleHandle(null), 0);
            if (_mouseHook == IntPtr.Zero)
                Logger.WriteLine("Quick panel: outside-click hook not installed, error " + Marshal.GetLastWin32Error());
        }

        private void UnhookOutsideClicks()
        {
            if (_mouseHook == IntPtr.Zero) return;
            UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        }

        /// <summary>
        /// Set on a press that landed outside the card, cleared by the release that ends
        /// it. Only a press and its own release together dismiss the panel.
        /// </summary>
        private bool _outsidePressPending;

        private IntPtr OnGlobalMouse(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code >= 0)
            {
                int message = (int)wParam;

                if (IsButtonDown(message))
                {
                    var hookData = Marshal.PtrToStructure<MouseLowLevelHookStruct>(lParam);
                    _outsidePressPending = !IsPointOnCard(hookData.Point);
                }
                else if (IsButtonUp(message) && _outsidePressPending)
                {
                    _outsidePressPending = false;

                    // Never close from inside the hook. Windows silently unhooks a
                    // low-level procedure that overruns its timeout, and hiding the panel
                    // means starting animations and running a layout pass.
                    Dispatcher.BeginInvoke(new Action(DismissForOutsideClick));
                }
            }

            // Always chain: the click belongs to whatever was clicked. The panel closing
            // is in addition to that, never instead of it.
            return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
        }

        private static bool IsButtonDown(int message)
            => message is WmLButtonDown or WmRButtonDown or WmMButtonDown or WmNcLButtonDown;

        private static bool IsButtonUp(int message)
            => message is WmLButtonUp or WmRButtonUp or WmMButtonUp or WmNcLButtonUp;

        /// <summary>
        /// Hit-tests against the visible card rather than the window. The window carries
        /// transparent slack for the shadow and the entrance animation, and on a detail
        /// page the card is bottom-aligned inside bounds that stay the height of the tile
        /// page - so a large part of the window is empty desktop as far as the user is
        /// concerned, and clicking it should dismiss.
        /// </summary>
        private bool IsPointOnCard(PointI point)
        {
            if (!IsVisible) return false;

            try
            {
                point = ToWindowCoordinates(point);

                System.Windows.Point topLeft = PanelChrome.PointToScreen(new System.Windows.Point(0, 0));
                System.Windows.Point bottomRight = PanelChrome.PointToScreen(
                    new System.Windows.Point(PanelChrome.ActualWidth, PanelChrome.ActualHeight));

                return point.X >= topLeft.X && point.X < bottomRight.X
                    && point.Y >= topLeft.Y && point.Y < bottomRight.Y;
            }
            catch
            {
                // No source yet, or mid-teardown, or a view transition between layout
                // passes. Treat an unanswerable hit test as on the card: a panel that
                // stays open one click too long is a nuisance, whereas dismissing here
                // eats the click that was meant for a control and looks like the button
                // did nothing. The activation watches still close the panel either way.
                return true;
            }
        }

        /// <summary>
        /// Brings a hook coordinate into the space <see cref="Visual.PointToScreen"/>
        /// answers in, so the two can be compared.
        /// </summary>
        /// <remarks>
        /// A low-level mouse hook reports per-monitor-aware screen coordinates - true
        /// physical pixels - whatever the receiving process is. This process is only
        /// system-DPI aware, so its own coordinates are fixed to the DPI that was the
        /// system's when it started. While those agree, comparing them directly works.
        ///
        /// They stop agreeing the moment the machine is on a display at a different
        /// scale from the one it booted on: plugging in an external monitor and letting
        /// the internal panel switch off is the ordinary way to get there. Windows then
        /// virtualises this process's coordinates, and every point PointToScreen returns
        /// is off by the ratio between the two scales. The card's rectangle lands
        /// somewhere the pointer can never be, so every press inside the panel tested as
        /// a press outside it and dismissed the panel - which read as the panel closing
        /// whenever anything in it was clicked.
        ///
        /// This is the conversion for exactly that: a no-op for a per-monitor-aware
        /// process, and the inverse of the virtualisation otherwise. It is left
        /// unconverted if the call fails, which is no worse than not calling it.
        /// </remarks>
        private PointI ToWindowCoordinates(PointI point)
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return point;

            PointI converted = point;
            return PhysicalToLogicalPointForPerMonitorDPI(hwnd, ref converted) ? converted : point;
        }

        private DateTime _shownAt = DateTime.MinValue;
        private DateTime _deactivateDismissedAt = DateTime.MinValue;
        private static readonly TimeSpan ActivationSettleTime = TimeSpan.FromMilliseconds(250);

        /// <summary>
        /// How far ahead of the reported press its deactivation may land. A mouse click
        /// deactivates us and reaches the icon in the same instant, but the taskbar takes
        /// touch natively at finger-down while a notify icon only hears about it through
        /// the mouse messages Windows synthesizes at finger-up, once the gesture
        /// recognizer has ruled out a drag or a press-and-hold. So a tap reports its press
        /// a whole tap-duration after it dismissed us, and this has to span that - a hold
        /// long enough to overrun it has already become a press-and-hold, not a tap.
        /// </summary>
        private static readonly TimeSpan TrayPressLead = TimeSpan.FromMilliseconds(700);

        /// <summary>
        /// A tray click takes activation away from the panel, and that Deactivated can
        /// arrive before the notify icon reports the press - so the panel is already
        /// closing by the time the click itself is delivered. Reports whether the close
        /// in flight belongs to that press, letting the click be spent on the close it
        /// caused instead of toggling the panel straight back open.
        ///
        /// Claiming a dismissal also spends it, so at most one click is ever absorbed:
        /// if the window above does misjudge an unrelated click, the next one still opens
        /// the panel rather than leaving the icon dead.
        /// </summary>
        internal bool TryClaimTrayPressDismissal(DateTime trayPressedAt)
        {
            if (_targetVisible) return false;
            if (_deactivateDismissedAt < trayPressedAt - TrayPressLead) return false;
            if (_deactivateDismissedAt > DateTime.UtcNow) return false;

            _deactivateDismissedAt = DateTime.MinValue;
            return true;
        }
    }
}
