using Arsenal.Application.Models;
using Arsenal.Application.Services.Contracts;
using Arsenal.UI;
using Arsenal.UI.Controls;
using Arsenal.UI.Converters;
using Arsenal.UI.ViewModels;
using Arsenal.UI.Views.Pages;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Ui = Wpf.Ui.Controls;

namespace UiControlsSmoke;

/// <summary>
/// Covers the shape of three UI changes that a compile cannot: on/off rows are switches
/// rather than action buttons, the driver card offers an in-application download instead
/// of a link out to the browser, and the navigation column folds itself away as the
/// window approaches its minimum width.
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main()
    {
        try
        {
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "config.json"), "{\"theme\":1}");

            var application = new App();
            application.InitializeComponent();
            App.ApplyConfiguredTheme();

            CheckAdvancedRowsAreSwitches();
            CheckDriverCardDownloadsInApp();
            CheckDriverRowsAreClickable();
            CheckResponsivePane();
            CheckResizeUncoversTheGroundNotWhite();
            CheckAltTabDoesNotRingAnElement();
            CheckSliderShowsItsValueWhileDragging();

            Console.WriteLine("UI controls smoke: OK");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("UI controls smoke FAILED: " + ex.Message);
            return 1;
        }
    }

    /// <summary>
    /// The four on/off rows on Advanced used to be action buttons whose caption flipped
    /// through BoolToTextConverter - "Turn on"/"Turn off", "Enable"/"Disable". A button
    /// that renames itself states the action; a switch states the state, which is what
    /// every other row on the page already does.
    /// </summary>
    private static void CheckAdvancedRowsAreSwitches()
    {
        // Null view model, as the other page smoke tools do: this is about what the
        // markup builds, and the page's own bindings need real hardware behind them.
        var page = new AdvancedPage(null!);
        var host = OffscreenHost(page);

        var buttons = Descendants(page).OfType<Ui.Button>().ToList();
        foreach (Ui.Button button in buttons)
        {
            // The caption is what gave these away, but it is a binding, and with no data
            // context there is no text to read. The binding is the evidence instead: a
            // button whose caption is a bool run through BoolToTextConverter is a button
            // that renames itself - "Turn on"/"Turn off", "Enable"/"Disable" - which is
            // the pattern being replaced. A switch shows the state and needs no caption.
            var binding = System.Windows.Data.BindingOperations.GetBinding(
                button, System.Windows.Controls.ContentControl.ContentProperty);

            Assert(binding?.Converter is not BoolToTextConverter,
                "Advanced still has a button that renames itself for an on/off state " +
                $"(ConverterParameter '{binding?.ConverterParameter}').");
        }

        int switches = Descendants(page).OfType<Ui.ToggleSwitch>().Count();
        Assert(switches >= 16, $"Advanced has only {switches} switches; an on/off row did not convert.");

        // ASUS background services is the deliberate exception: it is an operation
        // rather than a setting, so it stays a pair of actions naming what can be done
        // from where the services actually are. Checked by name because with no data
        // context there is no visibility to tell the two apart.
        var named = Descendants(page).OfType<Ui.Button>()
            .Select(button => button.Name)
            .Where(name => !string.IsNullOrEmpty(name))
            .ToHashSet(StringComparer.Ordinal);

        Assert(named.Contains("StartAsusServicesButton") && named.Contains("StopAsusServicesButton"),
            "The ASUS services row lost its start/stop actions.");

        Console.WriteLine($"  Advanced: {switches} switches, {buttons.Count} action buttons, " +
                          "none of them on/off, services start/stop intact");
        host.Close();

        CheckServicesRowFollowsState();
    }

    /// <summary>
    /// The services row has to offer the action that matches where the services actually
    /// are, and offer neither while it is working.
    /// </summary>
    /// <remarks>
    /// Driven from a stand-in rather than the real view model, whose constructor reads
    /// ACPI, the service manager and the touchpad driver. WPF binds by name, so an object
    /// carrying the same three properties exercises exactly the bindings the page
    /// declares - and unlike the real one it can be put into states on demand.
    /// </remarks>
    private static void CheckServicesRowFollowsState()
    {
        var page = new AdvancedPage(null!);
        var state = new ServicesStateStub();
        page.DataContext = state;
        var host = OffscreenHost(page);

        var start = FindByName(page, "StartAsusServicesButton");
        var stop = FindByName(page, "StopAsusServicesButton");
        var busy = FindByName(page, "AsusServicesBusyButton");

        void Expect(bool running, bool working, bool wantStart, bool wantStop, bool wantBusy)
        {
            state.Set(running, working);
            page.UpdateLayout();

            string label = $"running={running} working={working}";
            Assert(start.IsVisible == wantStart, $"{label}: Start services visibility was {start.IsVisible}.");
            Assert(stop.IsVisible == wantStop, $"{label}: Stop services visibility was {stop.IsVisible}.");
            Assert(busy.IsVisible == wantBusy, $"{label}: the working state visibility was {busy.IsVisible}.");
        }

        Expect(running: true, working: false, wantStart: false, wantStop: true, wantBusy: false);
        Expect(running: false, working: false, wantStart: true, wantStop: false, wantBusy: false);
        Expect(running: true, working: true, wantStart: false, wantStop: false, wantBusy: true);
        Expect(running: false, working: true, wantStart: false, wantStop: false, wantBusy: true);

        Console.WriteLine("  Services row: offers Stop when running, Start when stopped, neither while working");
        host.Close();
    }

    private static FrameworkElement FindByName(DependencyObject root, string name)
    {
        var match = Descendants(root).OfType<FrameworkElement>().FirstOrDefault(e => e.Name == name);
        if (match is null) throw new InvalidOperationException($"'{name}' is not in the visual tree.");
        return match;
    }

    private sealed class ServicesStateStub : System.ComponentModel.INotifyPropertyChanged
    {
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

        public bool AsusOptimizationRunning { get; private set; } = true;
        public bool IsAsusServiceOperationRunning { get; private set; }
        public bool CanStartAsusServices => !IsAsusServiceOperationRunning && !AsusOptimizationRunning;
        public bool CanStopAsusServices => !IsAsusServiceOperationRunning && AsusOptimizationRunning;

        public void Set(bool running, bool working)
        {
            AsusOptimizationRunning = running;
            IsAsusServiceOperationRunning = working;
            foreach (string name in new[]
            {
                nameof(AsusOptimizationRunning), nameof(IsAsusServiceOperationRunning),
                nameof(CanStartAsusServices), nameof(CanStopAsusServices)
            })
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
        }
    }

    /// <summary>
    /// The driver card used to be a HyperlinkButton pointing at the ASUS URL, which
    /// handed the download to the default browser. It now runs through the view model,
    /// and shows one of four states in the same slot.
    /// </summary>
    private static void CheckDriverCardDownloadsInApp()
    {
        var service = new StubUpdateService();
        var viewModel = new UpdatesViewModel(service);

        // Populated before the page is built so its Loaded handler sees a non-empty list
        // and does not go to the network.
        var states = new (string Title, DriverDownloadState State)[]
        {
            ("Idle package", DriverDownloadState.Idle),
            ("Downloading package", DriverDownloadState.Downloading),
            ("Ready package", DriverDownloadState.Ready),
            ("Failed package", DriverDownloadState.Failed)
        };

        foreach (var (title, state) in states)
        {
            var driver = new UpdateInfo
            {
                Title = title,
                LatestVersion = "1.0",
                DownloadUrl = "https://dlcdnets.asus.com/pub/ASUS/example.exe?model=X",
                State = UpdateState.Outdated,
                DownloadState = state,
                DownloadProgress = state == DriverDownloadState.Downloading ? 42 : -1,
                DownloadedPath = state == DriverDownloadState.Ready ? @"C:\example.exe" : string.Empty
            };
            viewModel.AsusUpdates.Add(driver);
            viewModel.VisibleDrivers.Add(driver);
        }

        var page = new UpdatesPage(viewModel);
        var host = OffscreenHost(page);

        // Scoped to each row's own container: every row is on screen at once, so a
        // page-wide search would pass just because some other row was in the state
        // being looked for.
        var list = Descendants(page).OfType<System.Windows.Controls.ItemsControl>()
            .FirstOrDefault(control => ReferenceEquals(control.ItemsSource, viewModel.VisibleDrivers));
        Assert(list is not null, "The driver list is not bound to VisibleDrivers.");

        foreach (UpdateInfo driver in viewModel.VisibleDrivers)
        {
            var container = list!.ItemContainerGenerator.ContainerFromItem(driver) as DependencyObject;
            Assert(container is not null, $"{driver.Title}: no row was realised.");

            var visible = Descendants(container!).OfType<FrameworkElement>().Where(e => e.IsVisible).ToList();
            string Actions() => string.Join(", ", visible.OfType<Ui.Button>().Select(b => b.Content?.ToString()));

            bool install = visible.Any(e => e.Name == "InstallButton");
            bool retry = visible.Any(e => e.Name == "RetryButton");
            bool progress = visible.Any(e => e.Name == "DownloadProgressChip");
            bool fetch = visible.Any(e => e.Name == "DownloadButton");

            // The remaining hyperlink buttons - Cancel, Show in folder - are commands,
            // not links, and NavigateUri defaults to an empty string rather than null,
            // so a non-empty one is what marks a control that leaves the application.
            // Only a failed row keeps that escape hatch.
            var links = visible.OfType<Ui.HyperlinkButton>()
                .Select(link => link.NavigateUri?.ToString() ?? string.Empty)
                .Where(uri => !string.IsNullOrWhiteSpace(uri))
                .ToList();

            Assert(links.Count == 0 || driver.DownloadState == DriverDownloadState.Failed,
                $"{driver.Title}: the row still sends the user out to a browser ({string.Join(", ", links)}).");

            switch (driver.DownloadState)
            {
                case DriverDownloadState.Idle:
                    Assert(fetch && !install && !retry && !progress,
                        $"{driver.Title}: an untouched row should offer only the download. Saw: {Actions()}");
                    break;
                case DriverDownloadState.Downloading:
                    Assert(progress && !install && !retry,
                        $"{driver.Title}: a row in flight should show progress and nothing else. Saw: {Actions()}");
                    break;
                case DriverDownloadState.Ready:
                    Assert(install && !retry && !progress,
                        $"{driver.Title}: a downloaded package offers no way to install it. Saw: {Actions()}");
                    break;
                case DriverDownloadState.Failed:
                    Assert(retry && !install && !progress,
                        $"{driver.Title}: a failed download offers no retry. Saw: {Actions()}");
                    Assert(links.Count > 0,
                        $"{driver.Title}: a failed download leaves no way through to ASUS.");
                    break;
            }
        }

        // The command the idle button fires has to exist and be the view model's.
        Assert(viewModel.DownloadDriverCommand is not null, "Download command missing.");
        Assert(viewModel.CancelDownloadCommand is not null, "Cancel command missing.");
        Assert(viewModel.InstallDriverCommand is not null, "Install command missing.");

        Console.WriteLine("  Drivers: four card states render, no row links out to a browser");
        host.Close();
    }

    /// <summary>
    /// Three things a driver row has to get right once its action is a button the user
    /// presses rather than a link that left the application: the page must not move
    /// under the pointer, the caption must fit, and the control must look pressable.
    /// </summary>
    private static void CheckDriverRowsAreClickable()
    {
        var viewModel = new UpdatesViewModel(new StubUpdateService());
        for (int i = 0; i < 25; i++)
        {
            var driver = new UpdateInfo
            {
                Title = $"Driver {i}",
                LatestVersion = "1.0",
                State = UpdateState.Outdated,
                DownloadUrl = "https://dlcdnets.asus.com/pub/ASUS/example.exe"
            };
            viewModel.AsusUpdates.Add(driver);
            viewModel.VisibleDrivers.Add(driver);
        }

        var page = new UpdatesPage(viewModel);
        var host = OffscreenHost(page);

        var scroller = Descendants(page).OfType<System.Windows.Controls.ScrollViewer>().First();
        var list = Descendants(page).OfType<System.Windows.Controls.ItemsControl>()
            .First(control => ReferenceEquals(control.ItemsSource, viewModel.VisibleDrivers));

        Ui.Button ActionFor(int index)
        {
            var row = (DependencyObject)list.ItemContainerGenerator.ContainerFromItem(viewModel.VisibleDrivers[index])!;
            return Descendants(row).OfType<Ui.Button>().First(b => b.Name == "DownloadButton");
        }

        Assert(scroller.ScrollableHeight > 200, "The list is too short to test scrolling against.");

        // Pressing a control focuses it, and WPF answers a focus change by asking the
        // scroller to bring it fully into view - which walked the page down on every
        // click. Parked at the bottom, focusing a row at the top makes that unmissable.
        scroller.ScrollToVerticalOffset(scroller.ScrollableHeight);
        host.UpdateLayout();
        double parked = scroller.VerticalOffset;

        System.Windows.Input.Keyboard.Focus(ActionFor(0));
        host.UpdateLayout();
        double moved = Math.Abs(scroller.VerticalOffset - parked);

        Assert(moved < 1, $"Pressing a driver action scrolled the page {moved:F0}px out from under the pointer.");

        var action = ActionFor(1);

        Assert(Equals(action.Cursor, System.Windows.Input.Cursors.Hand),
            $"A driver action does not show a pointer cursor (was {action.Cursor?.ToString() ?? "unset"}).");

        // The slot the button was actually given, before anything below disturbs it.
        double slot = action.ActualWidth;

        // Then ask the button what it would have liked. Reading DesiredSize as it stands
        // proves nothing: an explicit Width clamps DesiredSize to that same Width, so a
        // slot too narrow for its caption still reports a perfect fit. Clearing the width
        // first is what makes the content speak for itself - and is why this catches the
        // three pixels "Get update" was losing off its right edge. Measured rather than
        // compared to a pixel count, so it holds at any DPI and in any language.
        action.Width = double.NaN;
        action.InvalidateMeasure();
        action.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        double needed = action.DesiredSize.Width;

        Assert(slot + 0.5 >= needed,
            $"The action caption is clipped: a {slot:F0}px slot holding {needed:F0}px of content.");

        Console.WriteLine($"  Driver rows: click holds the scroll position, " +
                          $"{slot:F0}px slot fits {needed:F0}px of caption, pointer cursor");
        host.Close();

        CheckStartingADownloadMovesNothing();
    }

    /// <summary>
    /// Starting one download must not resize anything - not its own row, and not the
    /// other rows either.
    /// </summary>
    /// <remarks>
    /// The action cell sits in a shared-size column, so its width is negotiated across
    /// every row at once. A state whose content is wider or taller than the button it
    /// replaces therefore does not just disturb its own row: it widens the column for the
    /// whole list and pushes every other row's contents sideways. Measuring one row that
    /// changes and one that does not catches both halves.
    /// </remarks>
    private static void CheckStartingADownloadMovesNothing()
    {
        var viewModel = new UpdatesViewModel(new StubUpdateService());
        for (int i = 0; i < 6; i++)
        {
            var driver = new UpdateInfo
            {
                Title = $"Driver {i}",
                LatestVersion = "1.0",
                State = UpdateState.Outdated,
                FileSize = "12.4 MB",
                DownloadUrl = "https://dlcdnets.asus.com/pub/ASUS/example.exe"
            };
            viewModel.AsusUpdates.Add(driver);
            viewModel.VisibleDrivers.Add(driver);
        }

        var page = new UpdatesPage(viewModel);
        var host = OffscreenHost(page);

        var list = Descendants(page).OfType<System.Windows.Controls.ItemsControl>()
            .First(control => ReferenceEquals(control.ItemsSource, viewModel.VisibleDrivers));

        FrameworkElement CellOf(int index)
        {
            var row = (DependencyObject)list.ItemContainerGenerator.ContainerFromItem(viewModel.VisibleDrivers[index])!;
            var button = Descendants(row).OfType<FrameworkElement>().First(e => e.Name == "DownloadButton");
            return (FrameworkElement)System.Windows.Media.VisualTreeHelper.GetParent(button);
        }

        FrameworkElement RowOf(int index) =>
            (FrameworkElement)list.ItemContainerGenerator.ContainerFromItem(viewModel.VisibleDrivers[index])!;

        double cellWidthBefore = CellOf(0).ActualWidth;
        double otherCellWidthBefore = CellOf(3).ActualWidth;
        double rowHeightBefore = RowOf(0).ActualHeight;
        double otherRowHeightBefore = RowOf(3).ActualHeight;

        Assert(cellWidthBefore > 0 && rowHeightBefore > 0, "The driver rows did not lay out.");

        // Row 0 starts downloading and gets part-way, which is when the old layout grew.
        viewModel.VisibleDrivers[0].DownloadState = DriverDownloadState.Downloading;
        viewModel.VisibleDrivers[0].DownloadProgress = 47;
        host.UpdateLayout();

        Assert(Math.Abs(CellOf(0).ActualWidth - cellWidthBefore) < 0.5,
            $"Starting a download resized its own action cell: {cellWidthBefore:F0}px to {CellOf(0).ActualWidth:F0}px.");
        Assert(Math.Abs(CellOf(3).ActualWidth - otherCellWidthBefore) < 0.5,
            $"Starting a download in one row resized the action column for the rest: " +
            $"{otherCellWidthBefore:F0}px to {CellOf(3).ActualWidth:F0}px.");
        Assert(Math.Abs(RowOf(0).ActualHeight - rowHeightBefore) < 0.5,
            $"Starting a download changed its row height: {rowHeightBefore:F0}px to {RowOf(0).ActualHeight:F0}px.");
        Assert(Math.Abs(RowOf(3).ActualHeight - otherRowHeightBefore) < 0.5,
            "Starting a download changed another row's height.");

        // The chip has to be showing the progress, not just occupying the space.
        var line = Descendants(RowOf(0)).OfType<ProgressLine>().FirstOrDefault();
        Assert(line is not null, "The downloading chip has no progress line.");
        Assert(Math.Abs(line!.Value - 0.47) < 0.001,
            $"The progress line reads {line.Value:F2} for a download at 47%.");

        var percent = Descendants(RowOf(0)).OfType<System.Windows.Controls.TextBlock>()
            .FirstOrDefault(t => t.Text == "47%");
        Assert(percent is not null, "The downloading chip does not show the percentage.");

        // And the same when it finishes, which swaps in a differently-worded button.
        viewModel.VisibleDrivers[0].DownloadedPath = @"C:\example.exe";
        viewModel.VisibleDrivers[0].DownloadState = DriverDownloadState.Ready;
        host.UpdateLayout();

        Assert(Math.Abs(CellOf(3).ActualWidth - otherCellWidthBefore) < 0.5,
            "Finishing a download resized the action column for the other rows.");
        Assert(Math.Abs(RowOf(0).ActualHeight - rowHeightBefore) < 0.5,
            $"Finishing a download changed its row height: {rowHeightBefore:F0}px to {RowOf(0).ActualHeight:F0}px.");

        Console.WriteLine($"  Download states: {cellWidthBefore:F0}px action cell and {rowHeightBefore:F0}px row " +
                          "hold steady through downloading and ready");
        host.Close();
    }

    /// <summary>
    /// The rule, without a window: fold away near the minimum width, come back with
    /// room to spare, and never fight a user who worked the toggle in between.
    /// </summary>
    private static void CheckResponsivePane()
    {
        const double min = 900;
        var pane = new ResponsivePaneState();

        Assert(pane.Evaluate(1180, min, true) is null, "A comfortably wide window touched the pane.");

        Assert(pane.Evaluate(min, min, true) is false, "The pane did not fold away at the minimum width.");
        Assert(pane.IsNarrow, "The narrow band was not entered.");

        // Nothing further while it stays narrow - including at the collapse edge itself.
        Assert(pane.Evaluate(min + 10, min, false) is null, "The pane was touched again inside the band.");
        Assert(pane.Evaluate(min + ResponsivePaneState.CollapseMargin, min, false) is null,
            "The pane was touched again at the collapse edge.");

        // Between the two thresholds is deliberately dead, so a drag cannot oscillate.
        Assert(pane.Evaluate(min + ResponsivePaneState.ExpandMargin - 1, min, false) is null,
            "The pane came back before clearing the expand threshold.");

        Assert(pane.Evaluate(min + ResponsivePaneState.ExpandMargin, min, false) is true,
            "The pane did not come back once there was room.");
        Assert(!pane.IsNarrow, "The narrow band was not left.");

        // A user who had collapsed it by hand does not get it forced back open.
        var manual = new ResponsivePaneState();
        manual.UserSetPaneOpen(false);
        Assert(manual.Evaluate(min, min, false) is null, "A pane already closed was closed again.");
        Assert(manual.Evaluate(min + ResponsivePaneState.ExpandMargin, min, false) is null,
            "A pane the user had collapsed was forced back open.");

        Console.WriteLine($"  Pane: folds at <= {min + ResponsivePaneState.CollapseMargin}, " +
                          $"returns at >= {min + ResponsivePaneState.ExpandMargin}, honours a manual collapse");
    }

    /// <summary>
    /// Dragging a window edge outwards exposes area WPF has not drawn into yet, and what
    /// shows there is the composition target's clear colour. WPF's default is opaque
    /// white, and the library only makes it transparent on the Mica path - so with the
    /// backdrop off it stays white, and the edge being dragged flashes white until the
    /// next frame lands.
    /// </summary>
    private static void CheckResizeUncoversTheGroundNotWhite()
    {
        var ground = (App.Current.Resources["AppContentBackground"] as System.Windows.Media.SolidColorBrush)?.Color;
        Assert(ground is not null, "There is no content ground to match.");

        var window = new Ui.FluentWindow
        {
            Width = 420,
            Height = 300,
            Left = -20000,
            Top = -20000,
            ShowActivated = false,
            ExtendsContentIntoTitleBar = true,
            WindowBackdropType = Ui.WindowBackdropType.None,
            Content = new System.Windows.Controls.Grid()
        };
        window.Show();

        System.Windows.Media.Color Clear() =>
            ((System.Windows.Interop.HwndSource)PresentationSource.FromVisual(window)!).CompositionTarget.BackgroundColor;

        // Guards the premise as much as the fix: if WPF ever stops defaulting to white,
        // this test is measuring nothing and should say so rather than quietly pass.
        Assert(Clear() == System.Windows.Media.Colors.White,
            $"Expected WPF to start from white, but the clear colour was {Clear()} - this check has lost its subject.");

        IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;

        // DWM draws the frame - including the strip a resize exposes - from its own idea
        // of whether this is a dark window, not from anything the application paints.
        // The library only sets that as part of applying a backdrop, so starting opaque
        // left it light and the edge came out white until the setting was toggled.
        int darkBefore = 0;
        DwmGetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, out darkBefore, sizeof(int));

        App.ApplyWindowBackdrop(window);

        Assert(Clear() == ground!.Value,
            $"A resize would uncover {Clear()} rather than the {ground} ground.");

        int darkAfter = 0;
        DwmGetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, out darkAfter, sizeof(int));
        Assert(darkAfter == 1,
            $"DWM still treats the window as light ({darkAfter}), so it paints the resized edge light.");

        Console.WriteLine($"  Window resize: clear colour white -> {Clear()}, " +
                          $"DWM dark mode {darkBefore} -> {darkAfter}");
        window.Close();
    }

    private const int DwmwaUseImmersiveDarkMode = 20;

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);

    /// <summary>
    /// Returning to the window must not ring whatever happened to hold focus, and the
    /// ring must come straight back the moment the keyboard is used to navigate.
    /// </summary>
    private static void CheckAltTabDoesNotRingAnElement()
    {
        var button = new Ui.Button
        {
            Content = "Focusable",
            Style = (Style)App.Current.Resources["ActionButtonStyle"]
        };
        var host = OffscreenHost(button);
        button.Focus();

        Style? ringed = button.FocusVisualStyle;
        Assert(ringed is not null, "The test button has no focus visual, so this proves nothing.");

        var suppressor = new FocusRingSuppressor();

        suppressor.SuppressForActivation(button);
        Assert(button.FocusVisualStyle is null,
            "Alt+Tab would still ring the focused element.");
        Assert(ReferenceEquals(suppressor.Suppressed, button), "The suppressor lost track of the element.");

        // Coming back a second time without navigating must not overwrite the saved
        // value with the null already applied - that would strand the element ringless.
        suppressor.SuppressForActivation(button);

        suppressor.RestoreForNavigation();
        Assert(ReferenceEquals(button.FocusVisualStyle, ringed),
            $"Tabbing did not bring the ring back (got {button.FocusVisualStyle?.ToString() ?? "null"}).");
        Assert(suppressor.Suppressed is null, "The suppressor still claims an element.");

        Assert(FocusRingSuppressor.IsNavigationKey(System.Windows.Input.Key.Tab), "Tab is not treated as navigation.");
        Assert(!FocusRingSuppressor.IsNavigationKey(System.Windows.Input.Key.A), "A plain letter counts as navigation.");

        Console.WriteLine("  Alt+Tab: focus ring suppressed on activation, restored on the next Tab");
        host.Close();
    }

    /// <summary>
    /// Dragging a slider has to say what it is setting. The quick panel's sliders drop
    /// the readout column to buy track length, so while a drag was in progress the value
    /// was written nowhere on screen - and the pointer is on the thumb, which is the one
    /// part of the row that never carried it on any surface.
    /// </summary>
    /// <remarks>
    /// Driven through the thumb's own drag events rather than by asserting that the
    /// handlers are attached: the bubble has to open, word itself from the slider's
    /// format, follow the thumb, and close again, and only the first of those is
    /// visible from the wiring.
    /// </remarks>
    private static void CheckSliderShowsItsValueWhileDragging()
    {
        var slider = new ValueSlider
        {
            Minimum = 0,
            Maximum = 100,
            Value = 20,
            Format = "{0}%",
            ShowScale = false
        };

        var host = OffscreenHost(slider);

        var track = Descendants(slider).OfType<Track>().FirstOrDefault();
        Assert(track?.Thumb is not null, "The slider has no track and thumb, so there is nothing to drag.");
        Thumb thumb = track!.Thumb;

        var inner = Descendants(slider).OfType<System.Windows.Controls.Slider>().First();

        Assert(slider.ValueTooltip is null, "A slider nobody is touching already has a bubble.");

        thumb.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });

        Popup? bubble = slider.ValueTooltip;
        Assert(bubble is { IsOpen: true }, "Dragging the thumb raised no bubble.");

        var chrome = (System.Windows.Controls.Border)bubble!.Child;
        var text = (System.Windows.Controls.TextBlock)chrome.Child;
        Assert(text.Text == "20%",
            $"The bubble ignores the slider's format (showing \"{text.Text}\", expected \"20%\").");
        Assert(bubble.VerticalOffset < 0, "The bubble sits on the track rather than above it.");

        double atTwenty = bubble.HorizontalOffset;

        // A step of the drag, delivered the way a real one is: as a DragDelta bubbling
        // up from the thumb, which is what the slider works the new value out from. The
        // held slider has to let this through - and the hold, as first written, did not,
        // which would have shipped a brightness slider that could not be moved at all.
        thumb.RaiseEvent(new DragDeltaEventArgs(140, 0) { RoutedEvent = Thumb.DragDeltaEvent });
        host.UpdateLayout();

        double dragged = slider.Value;
        Assert(dragged > 20, $"The drag's own step did not move the slider (Value is {dragged}).");
        Assert(inner.Value == dragged, $"The thumb and the slider disagree ({inner.Value} against {dragged}).");
        Assert(text.Text == $"{Math.Round(dragged)}%", $"The bubble did not follow the drag (\"{text.Text}\").");
        Assert(bubble.HorizontalOffset > atTwenty, "The bubble did not move along the track with the thumb.");

        // The bubble is placed from the value, before the layout pass that moves the
        // thumb. This is that arithmetic checked against where the thumb actually
        // landed - the one thing a wrong assumption about the track would show up in.
        System.Windows.Point thumbOrigin = thumb.TransformToAncestor(track).Transform(new System.Windows.Point(0, 0));
        double thumbCentre = thumbOrigin.X + (thumb.ActualWidth / 2);
        double bubbleCentre = bubble.HorizontalOffset + (chrome.DesiredSize.Width / 2);
        Assert(Math.Abs(bubbleCentre - thumbCentre) < 1,
            $"The bubble is not over the thumb: centred at {bubbleCentre:0.##}, thumb at {thumbCentre:0.##}.");

        // The twitch: a slider bound to hardware hears its own writes come back, late
        // and stale, and applying one drags the thumb back to where the pointer was two
        // steps ago. Mid-drag, an outside write must not move anything.
        slider.Value = 12;
        host.UpdateLayout();
        Assert(slider.Value == dragged, $"A stale hardware report moved a thumb being dragged (to {slider.Value}).");
        Assert(inner.Value == dragged, $"The thumb itself was dragged back (to {inner.Value}).");
        Assert(text.Text == $"{Math.Round(dragged)}%", $"The bubble followed a stale report (\"{text.Text}\").");

        thumb.RaiseEvent(new DragCompletedEventArgs(0, 0, false) { RoutedEvent = Thumb.DragCompletedEvent });
        Assert(!bubble.IsOpen, "The bubble outlived the drag.");

        // The reports do not stop when the button comes up, so the hold outlasts the
        // drag - and then has to let go again, or the slider would stop answering the
        // brightness keys.
        slider.Value = 12;
        Assert(slider.Value == dragged, $"The hold ended with the drag (Value is {slider.Value}).");

        Pump(TimeSpan.FromMilliseconds(1100));
        slider.Value = 12;
        host.UpdateLayout();
        Assert(slider.Value == 12, $"The hold never let go: an outside write still reads {slider.Value}.");
        Assert(inner.Value == 12, $"The thumb did not follow the outside write (at {inner.Value}).");

        // A surface that has its own readout can turn the bubble off, and turning it off
        // while one is up has to take it down with it.
        thumb.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
        Assert(bubble.IsOpen, "The bubble does not come back for a second drag.");
        slider.ShowValueTooltip = false;
        Assert(!bubble.IsOpen, "ShowValueTooltip=false left a bubble on screen.");

        thumb.RaiseEvent(new DragCompletedEventArgs(0, 0, false) { RoutedEvent = Thumb.DragCompletedEvent });
        Console.WriteLine("  Slider: a drag raises a formatted value bubble over the thumb, " +
                          "and holds the thumb against stale hardware reports");
        host.Close();
    }

    /// <summary>
    /// Runs the dispatcher for a while, so that a DispatcherTimer gets to tick. Nothing
    /// here pumps messages on its own - the windows are shown but never run.
    /// </summary>
    private static void Pump(TimeSpan span)
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = span };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }

    private static Window OffscreenHost(UIElement content)
    {
        var host = new Window
        {
            Content = content,
            Width = 1180,
            Height = 900,
            WindowStyle = WindowStyle.None,
            ShowActivated = false,
            AllowsTransparency = true,
            Opacity = 0,
            Left = -20000,
            Top = -20000
        };
        host.Show();
        host.UpdateLayout();
        return host;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

#pragma warning disable CS0067

    private sealed class StubUpdateService : IUpdateService
    {
        public event Action<UpdateInfo>? UpdateStatusChanged;
        public Task<UpdateInfo> CheckForUpdatesAsync(bool force = false) => Task.FromResult(new UpdateInfo());
        public Task<bool> DownloadAndInstallUpdateAsync(IProgress<long>? progress = null, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<bool> DownloadAndInstallUpdateAsync(Arsenal.AutoUpdate.ReleaseUpdate release,
            IProgress<long>? progress = null, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<List<UpdateInfo>> CheckAsusUpdatesAsync() => Task.FromResult(new List<UpdateInfo>());
        public Task<string?> DownloadAsusPackageAsync(
            string downloadUrl, IProgress<int>? progress, CancellationToken cancellationToken, string? expectedSha256 = null)
            => Task.FromResult<string?>(null);
        public string DownloadFolder => Path.GetTempPath();
    }

#pragma warning restore CS0067
}
