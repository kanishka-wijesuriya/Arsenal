using System.Windows;
using System.Windows.Input;

namespace Arsenal.UI.Controls
{
    /// <summary>
    /// Keeps the focus ring that Alt+Tab brings back with it from showing.
    /// </summary>
    /// <remarks>
    /// Alt+Tab is keyboard input, so returning to the window makes WPF both restore
    /// keyboard focus to whatever held it and draw that element's focus visual. The window
    /// comes back looking as though the user had been tabbing around it, ringing an
    /// element they may not remember touching.
    ///
    /// The ring is suppressed rather than the focus moved. Clearing focus would stop the
    /// drawing just as well, but it would lose the caret in whatever was being typed
    /// before the user switched away, and would send the next Tab back to the top of the
    /// window instead of continuing from where they were. Only the single element that
    /// currently has focus is touched, and it is put back the moment the keyboard is
    /// genuinely used to navigate - so tabbing still shows exactly what it always did.
    ///
    /// Held apart from the window so the behaviour can be exercised without one.
    /// </remarks>
    public sealed class FocusRingSuppressor
    {
        private FrameworkElement? _suppressedOn;
        private object _suppressedValue = DependencyProperty.UnsetValue;

        /// <summary>The element whose ring is currently hidden, if any.</summary>
        public FrameworkElement? Suppressed => _suppressedOn;

        /// <summary>
        /// Hides the focus ring on whatever holds focus right now.
        /// </summary>
        /// <param name="focused">
        /// The focused element. Passed in rather than read from <see cref="Keyboard"/> so
        /// the decision can be exercised directly.
        /// </param>
        public void SuppressForActivation(IInputElement? focused)
        {
            if (focused is not FrameworkElement element) return;

            // Already hidden on this element: returning to the window twice must not
            // overwrite the saved value with the null we ourselves put there.
            if (ReferenceEquals(element, _suppressedOn)) return;

            RestoreForNavigation();

            _suppressedOn = element;
            _suppressedValue = element.ReadLocalValue(FrameworkElement.FocusVisualStyleProperty);
            element.FocusVisualStyle = null;
        }

        /// <summary>
        /// Gives the element its focus visual back, distinguishing a value it carried
        /// itself from one its style was providing - clearing the property outright would
        /// silently drop the former.
        /// </summary>
        public void RestoreForNavigation()
        {
            if (_suppressedOn is null) return;

            if (_suppressedValue == DependencyProperty.UnsetValue)
                _suppressedOn.ClearValue(FrameworkElement.FocusVisualStyleProperty);
            else
                _suppressedOn.FocusVisualStyle = (Style?)_suppressedValue;

            _suppressedOn = null;
            _suppressedValue = DependencyProperty.UnsetValue;
        }

        /// <summary>Whether a key is one that moves focus, and so should show the ring.</summary>
        public static bool IsNavigationKey(Key key)
            => key is Key.Tab or Key.Left or Key.Right or Key.Up or Key.Down;
    }
}
