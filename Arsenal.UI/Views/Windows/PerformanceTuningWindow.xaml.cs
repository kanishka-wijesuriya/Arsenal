using Arsenal.UI.ViewModels;
using Arsenal.UI.Views.Pages;
using System.Windows;
using Wpf.Ui.Controls;

namespace Arsenal.UI.Views.Windows
{
    public partial class PerformanceTuningWindow : FluentWindow
    {
        public PerformanceTuningWindow(PerformanceViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;
            EditorHost.Content = new PerformancePage(viewModel, compactEditor: true);
            App.ApplyWindowBackdrop(this);
            PreviewKeyDown += OnPreviewKeyDown;

            Closed += (_, _) =>
            {
                viewModel.CloseProfileManager();
                EditorHost.Content = null;
                DataContext = null;
            };
        }

        private void CloseWindow_Click(object sender, RoutedEventArgs e) => Close();

        private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key != System.Windows.Input.Key.Escape) return;

            e.Handled = true;
            Close();
        }
    }
}
