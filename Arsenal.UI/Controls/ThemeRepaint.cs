using System.Windows;

namespace Arsenal.UI.Controls
{
    /// <summary>
    /// Keeps a control that paints itself in step with the theme.
    /// </summary>
    /// <remarks>
    /// Anything built from a template repaints on its own: its brushes come through
    /// DynamicResource and swapping a theme replaces what they point at. A control that
    /// overrides OnRender and asks for a brush while it draws does not, because nothing
    /// asks it to draw again - so it keeps the colours of the theme it was last drawn
    /// under until it happens to be resized or its data changes. That is what made a
    /// theme switch look half-applied.
    ///
    /// <para>One call in a constructor rather than four lines repeated in every such
    /// control, and the unsubscribe is not optional: the event is static and lives for
    /// the life of the process, so a control still attached to it keeps its whole page
    /// alive - exactly what the tray release exists to prevent.</para>
    /// </remarks>
    internal static class ThemeRepaint
    {
        /// <summary>Redraws this element whenever the theme is applied.</summary>
        internal static void Follow(FrameworkElement element)
        {
            void Repaint() => element.InvalidateVisual();

            element.Loaded += (_, _) =>
            {
                App.ThemeChanged -= Repaint;
                App.ThemeChanged += Repaint;
            };

            element.Unloaded += (_, _) => App.ThemeChanged -= Repaint;
        }
    }
}
