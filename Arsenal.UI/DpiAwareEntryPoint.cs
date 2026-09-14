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

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
