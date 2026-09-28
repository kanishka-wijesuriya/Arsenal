using Arsenal.Application.Models;
using Arsenal.Application.Services.Contracts;
using Arsenal.Peripherals;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.Windows.Data;
using Wpf.Ui.Controls;

namespace Arsenal.UI.ViewModels
{
    /// <summary>An entry in a grouped list of things a button or key can be made to do.</summary>
    public sealed record BindingOption(int Code, string Name, string Group, ushort[]? ComboKeys = null);

    /// <summary>A list of binding targets, grouped under their headings.</summary>
    public static class BindingOptionList
    {
        public static ListCollectionView Grouped(List<BindingOption> options)
        {
            var view = new ListCollectionView(options);
            view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(BindingOption.Group)));
            return view;
        }
    }
}
