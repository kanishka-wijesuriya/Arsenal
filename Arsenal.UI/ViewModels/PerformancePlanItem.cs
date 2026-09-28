using CommunityToolkit.Mvvm.ComponentModel;
using Arsenal.Application.Models;
using Arsenal.Display;
using Arsenal.UI.Services;
using System.Collections.ObjectModel;
using Wpf.Ui.Controls;

namespace Arsenal.UI.ViewModels
{
    public partial class PerformancePlanItem : ObservableObject
    {
        public PerformancePlanItem(PerformancePlanInfo plan)
        {
            ModeIndex = plan.ModeIndex;
            Name = plan.Name;
            BaseMode = plan.BaseMode;
            IsCustom = plan.IsCustom;
        }

        public int ModeIndex { get; }
        public int BaseMode { get; }
        public bool IsCustom { get; }
        public SymbolRegular Icon => IsCustom
            ? SymbolRegular.Settings24
            : BaseMode switch
            {
                2 => SymbolRegular.Sleep24,
                1 => SymbolRegular.Flash24,
                _ => SymbolRegular.Gauge24
            };

        [ObservableProperty]
        private string _name;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsDeleteIdle))]
        private bool _isDeletePending;

        public bool IsDeleteIdle => IsCustom && !IsDeletePending;

        [ObservableProperty]
        private bool _isSelected;
    }
}
