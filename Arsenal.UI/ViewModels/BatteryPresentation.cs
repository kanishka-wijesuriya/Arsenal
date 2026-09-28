using Arsenal.Helpers;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Arsenal.Application.Models;
using Arsenal.Application.Services.Contracts;
using Arsenal.Display;
using Arsenal.UI.Services;
using System.Collections.ObjectModel;
using Wpf.Ui.Controls;

namespace Arsenal.UI.ViewModels
{
    /// <summary>
    /// Shared wording for the charge tiles on Home and Battery. Keeping the decision
    /// here prevents the two pages from drifting into different battery states.
    /// </summary>
    internal static class BatteryPresentation
    {
        public static string ChargeStopsAtText(int batteryPercent, int effectiveChargeLimit)
            => batteryPercent >= 100
                ? "Fully charged"
                : effectiveChargeLimit >= 100
                    ? "Charging to full"
                    : $"Charging stops at {effectiveChargeLimit}%";
    }
}
