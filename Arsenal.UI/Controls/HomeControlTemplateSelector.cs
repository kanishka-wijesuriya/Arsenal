using System.Windows;
using System.Windows.Controls;
using Arsenal.UI.ViewModels;

namespace Arsenal.UI.Controls
{
    /// <summary>Selects the native Home row for a persisted dashboard control.</summary>
    public sealed class HomeControlTemplateSelector : DataTemplateSelector
    {
        public DataTemplate? Performance { get; set; }
        public DataTemplate? Gpu { get; set; }
        public DataTemplate? ChargeLimit { get; set; }
        public DataTemplate? Refresh { get; set; }
        public DataTemplate? AutoRefresh { get; set; }
        public DataTemplate? Keyboard { get; set; }
        public DataTemplate? Touchpad { get; set; }
        public DataTemplate? FullCharge { get; set; }

        public override DataTemplate? SelectTemplate(object item, DependencyObject container) =>
            item is not HomeControlSlot slot ? base.SelectTemplate(item, container) : slot.Key switch
            {
                "performance" => Performance,
                "gpu" => Gpu,
                "charge_limit" => ChargeLimit,
                "refresh" => Refresh,
                "auto_refresh" => AutoRefresh,
                "keyboard" => Keyboard,
                "touchpad" => Touchpad,
                "full_charge" => FullCharge,
                _ => base.SelectTemplate(item, container)
            };
    }
}
