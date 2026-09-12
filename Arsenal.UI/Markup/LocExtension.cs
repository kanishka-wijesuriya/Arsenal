using Arsenal.Helpers;
using System.Windows.Markup;

namespace Arsenal.UI.Markup
{
    /// <summary>
    /// Resolves a string resource in XAML: <c>Text="{loc:Loc SettingsTitle}"</c>.
    /// The value is read once, when the element is created. Changing the display
    /// language takes effect on the next start, which is what the Settings page says.
    /// </summary>
    [MarkupExtensionReturnType(typeof(string))]
    public sealed class LocExtension : MarkupExtension
    {
        public LocExtension()
        {
        }

        public LocExtension(string key)
        {
            Key = key;
        }

        [ConstructorArgument("key")]
        public string Key { get; set; } = string.Empty;

        public override object ProvideValue(IServiceProvider serviceProvider) => AppStrings.Get(Key);
    }
}
