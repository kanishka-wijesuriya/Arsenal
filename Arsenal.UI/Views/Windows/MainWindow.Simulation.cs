#if ARSENAL_SIMULATOR
using System.Windows;

namespace Arsenal.UI.Views.Windows
{
    public partial class MainWindow
    {
        /// <summary>
        /// Marks a simulated session in the title bar and the taskbar.
        /// </summary>
        /// <remarks>
        /// First in the row of status pills, in the warning colour. Everything else on
        /// screen looks exactly as it would on the simulated laptop, which is the point,
        /// so this is the one thing that says the machine is not the one on the desk.
        /// </remarks>
        private void ShowSimulationBadge()
        {
            if (Arsenal.Simulation.DeviceSimulation.Profile is not { } profile) return;

            Title = "Arsenal, simulating " + profile.Name;

            var label = new System.Windows.Controls.TextBlock
            {
                Text = "Simulated " + (profile.Series.Length > 0 ? profile.Series : profile.Name),
                FontWeight = FontWeights.SemiBold,
                Style = (Style)FindResource("PillTextStyle"),
            };
            label.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "StatusWarning");

            var badge = new System.Windows.Controls.Border
            {
                Child = label,
                Style = (Style)FindResource("CompactStatusPillStyle"),
                ToolTip = $"{profile.Name}: firmware answers come from a profile and nothing reaches this PC's hardware. Close this window, or use Settings, Device simulator, to go back.",
            };
            badge.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty, "StatusWarning");

            StatusPills.Children.Insert(0, badge);
        }
    }
}
#endif
