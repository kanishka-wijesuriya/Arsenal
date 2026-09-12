using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Controls;

namespace Arsenal.UI.Controls
{
    /// <summary>
    /// A titled card that stacks <see cref="SettingsRow"/> children and draws the
    /// hairlines between them.
    ///
    /// Dividers are recomputed whenever a child is shown or hidden, so the
    /// capability-gated rows (Mini-LED, MUX, Slash, ...) never leave a stray line
    /// behind when the hardware does not support them.
    /// </summary>
    public class SettingsGroup : ItemsControl
    {

        public SettingsGroup()
        {
            Loaded += (_, _) => UpdateDividers();
            ItemContainerGenerator.StatusChanged += (_, _) =>
                Dispatcher.BeginInvoke(UpdateDividers, System.Windows.Threading.DispatcherPriority.Loaded);
        }

        public static readonly DependencyProperty HeaderProperty =
            DependencyProperty.Register(nameof(Header), typeof(string), typeof(SettingsGroup),
                new PropertyMetadata(null));

        /// <summary>Section title rendered above the card. Hidden when empty.</summary>
        public string? Header
        {
            get => (string?)GetValue(HeaderProperty);
            set => SetValue(HeaderProperty, value);
        }

        public static readonly DependencyProperty DescriptionProperty =
            DependencyProperty.Register(nameof(Description), typeof(string), typeof(SettingsGroup),
                new PropertyMetadata(null));

        /// <summary>One-line explanation under the section title. Hidden when empty.</summary>
        public string? Description
        {
            get => (string?)GetValue(DescriptionProperty);
            set => SetValue(DescriptionProperty, value);
        }

        public static readonly DependencyProperty IconProperty =
            DependencyProperty.Register(nameof(Icon), typeof(SymbolRegular), typeof(SettingsGroup),
                new PropertyMetadata(SymbolRegular.Empty));

        /// <summary>Optional glyph beside the section title.</summary>
        public SymbolRegular Icon
        {
            get => (SymbolRegular)GetValue(IconProperty);
            set => SetValue(IconProperty, value);
        }

        public static readonly DependencyProperty HeaderContentProperty =
            DependencyProperty.Register(nameof(HeaderContent), typeof(object), typeof(SettingsGroup),
                new PropertyMetadata(null));

        /// <summary>Optional action content aligned to the right of the section title.</summary>
        public object? HeaderContent
        {
            get => GetValue(HeaderContentProperty);
            set => SetValue(HeaderContentProperty, value);
        }

        public static readonly DependencyProperty FooterProperty =
            DependencyProperty.Register(nameof(Footer), typeof(object), typeof(SettingsGroup),
                new PropertyMetadata(null));

        /// <summary>Optional content rendered under the card, outside the row list.</summary>
        public object? Footer
        {
            get => GetValue(FooterProperty);
            set => SetValue(FooterProperty, value);
        }

        protected override void OnItemsChanged(System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            base.OnItemsChanged(e);

            foreach (object? item in Items)
            {
                if (item is not FrameworkElement element) continue;

                // Re-subscribing is harmless: the same handler instance for the same
                // element replaces nothing, so guard with an unsubscribe first.
                element.IsVisibleChanged -= OnChildVisibilityChanged;
                element.IsVisibleChanged += OnChildVisibilityChanged;
            }

            UpdateDividers();
        }

        private void OnChildVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e) => UpdateDividers();

        private void UpdateDividers()
        {
            bool isFirstVisible = true;

            for (int index = 0; index < Items.Count; index++)
            {
                object? item = Items[index];
                FrameworkElement? element = item as FrameworkElement;

                // Data-bound groups generate a ContentPresenter whose template contains
                // the SettingsRow. Static groups already give us the row directly.
                if (element is null && ItemContainerGenerator.ContainerFromIndex(index) is DependencyObject container)
                    element = FindSettingsRow(container);
                if (element is null) continue;

                element.IsVisibleChanged -= OnChildVisibilityChanged;
                element.IsVisibleChanged += OnChildVisibilityChanged;
                if (element.Visibility != Visibility.Visible) continue;

                if (element is SettingsRow row)
                    row.ShowDivider = !isFirstVisible;

                isFirstVisible = false;
            }
        }

        private static SettingsRow? FindSettingsRow(DependencyObject root)
        {
            if (root is SettingsRow row) return row;
            int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
            for (int index = 0; index < count; index++)
                if (FindSettingsRow(System.Windows.Media.VisualTreeHelper.GetChild(root, index)) is { } nested)
                    return nested;
            return null;
        }
    }
}
