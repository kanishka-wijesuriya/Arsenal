using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Arsenal.Application.Models;
using Arsenal.Application.Services.Contracts;
using Arsenal.Helpers;
using Arsenal.Mode;
using Arsenal.UI.Services;
using Arsenal.UI.Services.Remote;
using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;

namespace Arsenal.UI.ViewModels
{
    public partial class CommandPaletteViewModel : ObservableObject
    {
        private readonly ISettingsSearchService _searchService;

        [ObservableProperty]
        private string _searchQuery = string.Empty;

        [ObservableProperty]
        private ObservableCollection<SearchItem> _results = new();

        [ObservableProperty]
        private SearchItem? _selectedItem;

        public CommandPaletteViewModel(ISettingsSearchService searchService)
        {
            _searchService = searchService;
            UpdateResults();
        }

        partial void OnSearchQueryChanged(string value)
        {
            UpdateResults();
        }

        /// <summary>True only when a query was typed and matched nothing.</summary>
        public bool HasNoResults => Results.Count == 0 && !string.IsNullOrWhiteSpace(SearchQuery);

        private void UpdateResults()
        {
            Results.Clear();
            var matches = _searchService.Search(SearchQuery);
            foreach (var item in matches)
                Results.Add(item);

            // Always preselect the top hit, so Enter runs the best match without any
            // arrow keys. Clearing it when empty stops Enter firing a stale selection.
            SelectedItem = Results.Count > 0 ? Results[0] : null;
            OnPropertyChanged(nameof(HasNoResults));
        }

        [RelayCommand]
        public void ExecuteSelected()
        {
            if (SelectedItem?.Action != null)
            {
                SelectedItem.Action.Invoke();
            }
        }
    }
}
