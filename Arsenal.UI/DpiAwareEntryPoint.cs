namespace Arsenal.UI;

/// <summary>
/// Applies the process-wide DPI mode before WPF or Windows Forms creates a window.
/// This lets every Arsenal window re-render when it crosses between monitors with
/// different display scaling instead of being bitmap-scaled by Windows.
/// </summary>
internal static class DpiAwareEntryPoint
{
    [STAThread]
    public static void Main()
    {
        ApplicationConfiguration.Initialize();

        // Before anything draws. Windows reads this when the first Direct3D device is
        // created, and a control panel belongs on the integrated adapter.
        Arsenal.Helpers.GpuPreference.PreferIntegrated();
        ApplyRenderMode();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }

    /// <summary>
    /// Draws on the processor instead of the graphics card, when asked to.
    /// </summary>
    /// <remarks>
    /// Here rather than anywhere else because the mode is fixed for the life of the
    /// process the moment WPF composes its first window, so there is exactly one place
    /// it can be set: before one exists.
    ///
    /// <para>What it buys, measured on this machine with a bare window: private memory
    /// 92MB down to 44MB, working set 139MB down to 79MB. The graphics libraries are
    /// still mapped - the driver loads them when a window is created either way - but
    /// the surfaces and driver heaps behind them are what the commit was, and those go.
    /// </para>
    ///
    /// <para>What it costs is every animation and the smooth scrolling, drawn by the CPU
    /// on a 3440x1440 panel. That is why it is off unless asked for: it is a trade, not
    /// an improvement, and only the person looking at the screen can say which side they
    /// want to be on.</para>
    /// </remarks>
    private static void ApplyRenderMode()
    {
        try
        {
            if (!AppConfig.Is("software_render")) return;

            System.Windows.Media.RenderOptions.ProcessRenderMode =
                System.Windows.Interop.RenderMode.SoftwareOnly;
            Logger.WriteLine("Software rendering is on; the graphics card will not be used to draw.");
        }
        catch (Exception ex)
        {
            Logger.WriteLine("Render mode: " + ex.Message);
        }
    }
}
