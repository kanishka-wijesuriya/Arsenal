using Arsenal.Application.Models;
using Arsenal.AutoUpdate;
using Arsenal.Display;

namespace Arsenal.Application.Services.Contracts
{
    public interface ISettingsSearchService
    {
        IReadOnlyList<SearchItem> Search(string query);
        void RegisterItem(SearchItem item);
    }
}
