namespace Arsenal.UI.Views.Windows;

/// <summary>
/// Small navigation state that survives disposal of the hidden Main Window.
/// </summary>
public sealed class MainWindowState
{
    public List<string> History { get; } = new();
    public int HistoryIndex { get; set; } = -1;
}
