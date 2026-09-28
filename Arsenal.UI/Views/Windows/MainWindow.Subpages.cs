using Arsenal.AutoUpdate;
using Arsenal.Helpers;
using Arsenal.UI.ViewModels;
using Arsenal.UI.Views.Overlays;
using Arsenal.UI.Views.Pages;
using Microsoft.Extensions.DependencyInjection;
using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Wpf.Ui.Controls;

namespace Arsenal.UI.Views.Windows
{
    public partial class MainWindow
    {
        /// <summary>
        /// Pressing the page half of the path leaves the subpage standing on it.
        /// </summary>
        /// <remarks>
        /// Not <see cref="GoBack"/>, even though it does the same thing today. This
        /// button names one destination - the page written on it - and it must go there
        /// and nowhere else; the arrow's job is to undo the last step, whatever that
        /// was, and the two would part company the moment a subpage could hold another.
        /// </remarks>
        private void BreadcrumbRootLink_Click(object sender, RoutedEventArgs e)
            => Controls.SettingsGroup.Close();

        private void SubpageBackButton_Click(object sender, RoutedEventArgs e)
            => Controls.SettingsGroup.Close();

        // ---- The path in the title bar ------------------------------------------

        private static readonly Duration CrumbIn = new(TimeSpan.FromMilliseconds(260));
        private static readonly Duration CrumbOut = new(TimeSpan.FromMilliseconds(170));

        /// <summary>
        /// Each page's own title and description lines, and the words they started with.
        /// </summary>
        /// <remarks>
        /// Found by walking the page once rather than by every page exposing them,
        /// because the alternative was editing a dozen page headers to say something
        /// the shared styles already say. Keyed on the page instance, which is cached
        /// and reused, so the originals survive any number of trips in and out of a
        /// subpage.
        /// </remarks>
        private readonly Dictionary<UIElement, (System.Windows.Controls.TextBlock Line, string Original)> _pageTitles = new();
        private readonly Dictionary<UIElement, (System.Windows.Controls.TextBlock Line, string Original)> _pageSubtitles = new();

        private void OnOpenGroupChanged(Controls.SettingsGroup? group)
        {
            // Opening anything, including the one forward just returned to, settles
            // where the user is. Only the close in GoBack leaves an offer standing.
            if (group is not null) _subpageBackedOutOf = null;

            // Raised from whichever page the group is on, which is this thread, but a
            // group closing during teardown can arrive while the bar is already gone.
            if (!IsLoaded) return;

            ShowSubpageCrumb(group?.Header);
            ShowSubpageHeader(group, animate: !_leavingPage);
            UpdateHistoryButtons();
        }

        /// <summary>
        /// Set while a page change closes the group it is leaving behind.
        /// </summary>
        /// <remarks>
        /// That close still has to hand the page back its own title, but the page is
        /// about to be taken off screen and there is nobody to show the handover to.
        /// Animating it would also leave a page mid-fade in the cache, so the next visit
        /// began with the header climbing into place for no reason.
        /// </remarks>
        private bool _leavingPage;

        /// <summary>Left inset of a page's own content, from the pane seam.</summary>
        /// <remarks>Matches the left component of the shared PagePadding thickness.</remarks>
        private const double PageContentInset = 32d;

        /// <summary>
        /// The earliest the path can start without being drawn through the bar's own
        /// controls.
        /// </summary>
        /// <remarks>
        /// Four 40px buttons on a 40px pitch, inset 4px from the window edge, and a gap
        /// after the last of them. With the pane open the content edge is well past
        /// this; with the pane collapsed the content edge is back behind the buttons,
        /// and this is what keeps the path clear of them.
        /// </remarks>
        private const double BreadcrumbMinimumLeft = 176d;

        /// <summary>
        /// Room kept clear on the right for the status pills and the caption buttons.
        /// </summary>
        /// <remarks>
        /// Not a divider between them, just the width the path is never allowed to
        /// reach: it trims itself at that point instead of running under the pills. The
        /// pills are a little narrower than this at most readings, which leaves a gap
        /// rather than a collision when they are at their widest.
        ///
        /// <para>Raised when the elevation pill joined the row. It has to clear the
        /// widest the pills ever get, which is the standard-user wording rather than the
        /// shorter one an elevated instance shows.</para>
        /// </remarks>
        private const double BreadcrumbRightReserve = 600d;

        /// <summary>
        /// Lines the path up with the left edge of the page's own content.
        /// </summary>
        /// <remarks>
        /// Driven from the same seam the window grounds use, so it tracks the pane frame
        /// by frame as it opens and closes instead of jumping to its destination while
        /// the pane is still travelling.
        /// </remarks>
        private void PlaceBreadcrumb(double seam)
        {
            double left = Math.Max(seam + PageContentInset, BreadcrumbMinimumLeft);

            // This runs inside a layout pass; writing the value back unchanged would
            // invalidate layout again for nothing.
            if (Math.Abs(Breadcrumb.Margin.Left - left) < 0.5) return;
            Breadcrumb.Margin = new Thickness(left, 0, BreadcrumbRightReserve, 0);
        }

        /// <summary>
        /// Slides the whole path in, or takes it away.
        /// </summary>
        /// <remarks>
        /// All of it, not just the trailing segment. On a top-level page the sidebar's
        /// own selection and the page's own title both name where you are, and a bar
        /// that said it a third time was three labels for one place; the path earns its
        /// space only once there is a step in it.
        ///
        /// <para>Render-only and released on completion, for the same reason the page
        /// arrival is: the bar outlives every page, and an animation left holding its
        /// final value outranks whatever sets opacity on it next.</para>
        /// </remarks>
        private void ShowSubpageCrumb(string? subpage)
        {
            bool wanted = !string.IsNullOrWhiteSpace(subpage);
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

            if (wanted)
            {
                // Both halves are written together, here, rather than the page half being
                // kept up to date as you navigate. Navigating away from an open subpage
                // starts this path fading out, and a page name rewritten underneath that
                // fade put the new page beside the old subpage - "Display > CPU power" -
                // for as long as the fade lasted. Nothing writes to a path on its way out
                // now; the next one to arrive states both of its own halves.
                BreadcrumbRoot.Text = PageDisplayName(_viewModel.ActivePageTag ?? "Home");
                BreadcrumbLeaf.Text = subpage;
                Breadcrumb.Visibility = Visibility.Visible;

                Breadcrumb.BeginAnimation(OpacityProperty, new DoubleAnimation(1, CrumbIn) { EasingFunction = ease });
                BreadcrumbShift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, CrumbIn) { EasingFunction = ease });
                return;
            }

            if (Breadcrumb.Visibility != Visibility.Visible) return;

            var fade = new DoubleAnimation(0, CrumbOut) { EasingFunction = ease };
            fade.Completed += (_, _) =>
            {
                // Only if nothing opened again while this was running, or the path that
                // just arrived would be hidden by the departure of the last one.
                if (Controls.SettingsGroup.OpenGroup is not null) return;
                Breadcrumb.BeginAnimation(OpacityProperty, null);
                Breadcrumb.Opacity = 0;
                Breadcrumb.Visibility = Visibility.Collapsed;
            };

            Breadcrumb.BeginAnimation(OpacityProperty, fade);
            BreadcrumbShift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-10, CrumbOut) { EasingFunction = ease });
        }

        /// <summary>
        /// Puts the subpage's name and description on the page's own header.
        /// </summary>
        /// <remarks>
        /// A subpage fills the page, so the page's title is the title of what is on
        /// screen. Leaving it on the parent's name meant the heading contradicted both
        /// the path in the bar and everything underneath it.
        ///
        /// <para>Both lines at once, through one animation on the block that holds them.
        /// They used to fade separately, which is two clocks for one change: the large
        /// title and the small line under it blinked out and back independently, and
        /// because each faded to nothing and then swapped its own words, the header spent
        /// a moment empty in the middle of it. One block, one motion, and the words are
        /// exchanged while nothing is showing.</para>
        ///
        /// <para>The way back out sits above the title, arriving and leaving with the
        /// words rather than on a clock of its own.</para>
        /// </remarks>
        private void ShowSubpageHeader(Controls.SettingsGroup? group, bool animate)
        {
            if (PageHeaderLine("PageTitleStyle", _pageTitles) is not { } title) return;
            var subtitle = PageHeaderLine("PageSubtitleStyle", _pageSubtitles);

            string wantedTitle = string.IsNullOrWhiteSpace(group?.Header) ? title.Original : group!.Header!;
            string? wantedSubtitle = subtitle is null
                ? null
                : string.IsNullOrWhiteSpace(group?.Description) ? subtitle.Value.Original : group!.Description!;

            // The page the way back leads to, read now rather than when it is pressed:
            // this is the page the subpage was opened from, and it is what the control
            // has to be labelled with.
            string? backTo = string.IsNullOrWhiteSpace(group?.Header)
                ? null
                : PageDisplayName(_viewModel.ActivePageTag ?? "Home");

            FrameworkElement? block = HeaderBlock(title.Line, subtitle?.Line);

            bool changes = !string.Equals(title.Line.Text, wantedTitle, StringComparison.Ordinal)
                || (subtitle is { } current && !string.Equals(current.Line.Text, wantedSubtitle, StringComparison.Ordinal));
            if (!changes) return;

            // Taken now, not read back out of the host inside Swap: an animated swap runs
            // that a tenth of a second later, by which time the host can have been
            // emptied by the window going to the tray.
            UIElement page = PageContentHost.Children[0];

            void Swap()
            {
                title.Line.Text = wantedTitle;
                if (subtitle is { } line && wantedSubtitle is not null) line.Line.Text = wantedSubtitle;
                ShowSubpageBackButton(page, block as System.Windows.Controls.Panel, title.Line, backTo);
            }

            if (!animate || block is null)
            {
                Swap();
                return;
            }

            ReplaceHeaderWords(block, Swap, ++_headerSwap, () => _headerSwap);
        }

        /// <summary>
        /// The way back out of a subpage, kept per page and shown above its title.
        /// </summary>
        /// <remarks>
        /// One per page, made when that page first needs one and then reused, so the
        /// button does not churn on every trip in and out of a group. Keyed on the page
        /// exactly as the header lines are, and dropped with them when the window
        /// returns to the tray.
        /// </remarks>
        private readonly Dictionary<UIElement, System.Windows.Controls.Button> _subpageBackButtons = new();

        /// <summary>
        /// Puts the control above the page's title, or takes it away.
        /// </summary>
        /// <remarks>
        /// Inserted into the page's own header block rather than written into a dozen
        /// page headers by hand, for the same reason the title and description are found
        /// by walking the page: the alternative is the same four lines of XAML repeated
        /// on every page that owns a group, each free to drift from the others.
        ///
        /// <para>Inside the block that fades, deliberately, so it arrives with the title
        /// it belongs to. A control appearing at full strength above a header still
        /// dissolving would be the one thing on screen not taking part in the change.
        /// </para>
        /// </remarks>
        private void ShowSubpageBackButton(UIElement page, System.Windows.Controls.Panel? header, System.Windows.Controls.TextBlock title, string? backTo)
        {
            if (header is null) return;

            if (backTo is null)
            {
                if (_subpageBackButtons.TryGetValue(page, out var open))
                    header.Children.Remove(open);
                return;
            }

            if (!_subpageBackButtons.TryGetValue(page, out var button))
            {
                button = new System.Windows.Controls.Button();

                // The key, not the style it resolves to right now. Assigning the object
                // would pin this button to whichever theme was on when it was made, and
                // these are kept for the life of the window.
                button.SetResourceReference(StyleProperty, "SubpageBackButtonStyle");
                button.Click += SubpageBackButton_Click;
                _subpageBackButtons[page] = button;
            }

            // "Go back", not the page's name. The name is already on screen twice by
            // the time this is showing - in the path above it and on the sidebar item
            // still marked active - and a third copy read as a heading rather than as
            // something to press. The tooltip still says where it goes.
            button.Content = "Go back";
            button.ToolTip = $"Back to {backTo}";

            // Directly above the title, wherever the title happens to sit: a page may
            // carry something else in its header block, and index 0 is not reliably the
            // line this belongs over.
            int at = header.Children.IndexOf(title);
            if (header.Children.Contains(button)) return;
            header.Children.Insert(at < 0 ? 0 : at, button);
        }

        /// <summary>
        /// Counts header changes so a swap that is overtaken by the next one stops
        /// rather than finishing on top of it.
        /// </summary>
        /// <remarks>
        /// Reachable: a group's header carries a way back into the list, so opening one
        /// and immediately leaving it is an ordinary double-click away, and both trips
        /// animate the same two lines.
        /// </remarks>
        private int _headerSwap;

        /// <remarks>
        /// The two together come to the 320ms a page takes to arrive, so drilling into a
        /// subpage and navigating to a page are the same length of event.
        /// </remarks>
        private static readonly Duration HeaderWordsOut = new(TimeSpan.FromMilliseconds(110));
        private static readonly Duration HeaderWordsIn = new(TimeSpan.FromMilliseconds(210));

        /// <summary>How far the header's words drop before rising back into place.</summary>
        /// <remarks>
        /// Small, and upwards on the way in, so the header arrives the way a page does.
        /// A pure fade with no travel at all reads as a blink rather than as a change of
        /// subject, which is what made the old swap look like a glitch.
        /// </remarks>
        private const double HeaderTravel = 7d;

        private static void ReplaceHeaderWords(FrameworkElement block, Action swap, int token, Func<int> current)
        {
            // Kept on the block rather than hung up and taken down again: the header is
            // animated repeatedly over a page's life, and reusing one transform means a
            // swap that arrives mid-flight continues from where the last one had reached
            // instead of snapping back to the start.
            if (block.RenderTransform is not TranslateTransform shift)
            {
                shift = new TranslateTransform();
                block.RenderTransform = shift;
            }

            var leaving = new CubicEase { EasingMode = EasingMode.EaseIn };
            var arriving = new CubicEase { EasingMode = EasingMode.EaseOut };

            var fadeOut = new DoubleAnimation(0, HeaderWordsOut) { EasingFunction = leaving };
            fadeOut.Completed += (_, _) =>
            {
                if (current() != token) return;
                swap();

                // Released before the return leg so the new animation starts from a value
                // this one is no longer holding down.
                block.BeginAnimation(OpacityProperty, null);
                block.Opacity = 0;
                shift.BeginAnimation(TranslateTransform.YProperty, null);
                shift.Y = HeaderTravel;

                var fadeIn = new DoubleAnimation(1, HeaderWordsIn) { EasingFunction = arriving };
                fadeIn.Completed += (_, _) =>
                {
                    if (current() != token) return;
                    block.BeginAnimation(OpacityProperty, null);
                    block.Opacity = 1;
                    shift.BeginAnimation(TranslateTransform.YProperty, null);
                    shift.Y = 0;
                };

                block.BeginAnimation(OpacityProperty, fadeIn);
                shift.BeginAnimation(TranslateTransform.YProperty,
                    new DoubleAnimation(0, HeaderWordsIn) { EasingFunction = arriving });
            };

            block.BeginAnimation(OpacityProperty, fadeOut);
            shift.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(-HeaderTravel * 0.5, HeaderWordsOut) { EasingFunction = leaving });
        }

        /// <summary>
        /// The smallest thing on the page that holds both header lines and nothing that
        /// answers the mouse.
        /// </summary>
        /// <remarks>
        /// Not the page's header grid, which on some pages carries a control beside the
        /// text: fading and sliding that would animate something the user can click, and
        /// a button that moves while being aimed at is a button that gets missed. The
        /// walk stops at the pair of lines and goes no further out.
        /// </remarks>
        private static FrameworkElement? HeaderBlock(
            System.Windows.Controls.TextBlock title,
            System.Windows.Controls.TextBlock? subtitle)
        {
            if (subtitle is null) return title;

            for (DependencyObject? node = VisualTreeHelper.GetParent(title); node is not null;
                 node = VisualTreeHelper.GetParent(node))
            {
                if (node is not FrameworkElement element) continue;
                if (Descendants(element).Any(child => ReferenceEquals(child, subtitle))) return element;
            }
            return title;
        }

        /// <summary>
        /// The line of the open page's header drawn with the given style, and the words
        /// it was born with.
        /// </summary>
        private (System.Windows.Controls.TextBlock Line, string Original)? PageHeaderLine(
            string styleKey,
            Dictionary<UIElement, (System.Windows.Controls.TextBlock Line, string Original)> cache)
        {
            if (PageContentHost.Children.Count != 1) return null;
            UIElement page = PageContentHost.Children[0];
            if (cache.TryGetValue(page, out var known)) return known;

            if (TryFindResource(styleKey) is not Style style) return null;

            System.Windows.Controls.TextBlock? found = Descendants(page)
                .OfType<System.Windows.Controls.TextBlock>()
                .FirstOrDefault(text => ReferenceEquals(text.Style, style));
            if (found is null) return null;

            var entry = (found, found.Text);
            cache[page] = entry;
            return entry;
        }

        private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
        {
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int index = 0; index < count; index++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(root, index);
                yield return child;
                foreach (DependencyObject descendant in Descendants(child)) yield return descendant;
            }
        }
    }
}
