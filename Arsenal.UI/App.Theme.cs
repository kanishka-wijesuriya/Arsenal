using Arsenal.Application.Models;
using Arsenal.Application.Services.Contracts;
using Arsenal.AutoUpdate;
using Arsenal.Battery;
using Arsenal.Display;
using Arsenal.Gpu;
using Arsenal.Helpers;
using Arsenal.Peripherals;
using Arsenal.UI.ViewModels;
using Arsenal.UI.Services;
using Arsenal.UI.Views.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using WpfUi = Wpf.Ui.Appearance;

namespace Arsenal.UI
{
    public partial class App
    {
        /// <summary>The palette in force. Read by anything that has to branch on it.</summary>
        public static Theming.ThemePalette Palette { get; private set; } = Theming.ThemePalette.Dark;

        /// <summary>
        /// Raised once a theme has been applied in full.
        /// </summary>
        /// <remarks>
        /// For the handful of controls that draw themselves. A template picks its
        /// colours up through DynamicResource and repaints on its own, but a control
        /// that reads a brush inside OnRender only reads it again when something asks it
        /// to redraw, and swapping a theme asks nothing of anybody: the fan curve, the
        /// slider scales and the capacity chart all kept the colours of the theme they
        /// were last drawn under until they were resized or their data changed.
        ///
        /// <para>Subscribe on Loaded and drop it on Unloaded. This is static and lives
        /// as long as the process, so a control that holds on to it holds its whole page
        /// with it.</para>
        /// </remarks>
        public static event Action? ThemeChanged;

        public static void ApplyConfiguredTheme()
        {
            try
            {
                Theming.ThemePalette palette = ConfiguredPalette();
                Palette = palette;

                // WPF UI's control theme and the DWM title bar still know only two
                // states, so a third palette declares which of the two it is built on.
                WpfUi.ApplicationThemeManager.Apply(palette.IsLight
                    ? WpfUi.ApplicationTheme.Light
                    : WpfUi.ApplicationTheme.Dark);

                if (Current != null)
                {
                    Current.Resources["SurfaceBase"] = Brush(palette.SurfaceBase);
                    Current.Resources["SurfaceLayer"] = Brush(palette.SurfaceLayer);
                    Current.Resources["SurfaceCard"] = Brush(palette.SurfaceCard);
                    Current.Resources["SurfaceCardHover"] = Brush(palette.SurfaceCardHover);
                    Current.Resources["SurfaceSunken"] = Brush(palette.SurfaceSunken);
                    Current.Resources["StrokeSubtle"] = Brush(palette.StrokeSubtle);
                    Current.Resources["StrokeDivider"] = Brush(palette.StrokeDivider);
                    Current.Resources["TextPrimary"] = Brush(palette.TextPrimary);
                    Current.Resources["TextSecondary"] = Brush(palette.TextSecondary);
                    Current.Resources["TextTertiary"] = Brush(palette.TextTertiary);
                    Current.Resources["StatusSuccess"] = Brush(palette.StatusSuccess);
                    Current.Resources["StatusWarning"] = Brush(palette.StatusWarning);
                    Current.Resources["StatusCritical"] = Brush(palette.StatusCritical);

                    Current.Resources["DividerBrush"] = FadedLine(palette.DividerLine);
                    ApplyThemeChrome(palette);
                    ApplyShapeResources(palette);
                    ApplyTypeResources(palette);
                    ApplyNavigationForeground(palette);
                    ApplyAccentResources(palette);
                    ApplyWindowSurfaces(palette);

                    // Last, once every token and template is in place.
                    ThemeChanged?.Invoke();
                }
            }
            catch (Exception ex)
            {
                Logger.WriteLine("Theme apply error: " + ex.Message);
            }
        }

        /// <summary>
        /// Corner radius and border weight, which used not to be a theme's business.
        /// </summary>
        /// <remarks>
        /// Every consumer of these reads them with DynamicResource for this reason: a
        /// StaticResource is resolved once when the tree is loaded and would keep the
        /// radius of whichever theme happened to be in force at startup.
        /// </remarks>
        /// <summary>
        /// The dictionary of replacement control templates a theme brings with it.
        /// </summary>
        /// <remarks>
        /// Merged last and removed again, because the winning entry for a duplicated key
        /// is the one merged latest: a theme's templates have to arrive after the stock
        /// ones they replace, and have to leave when the theme does or a switch away
        /// would keep its chrome with another theme's colours.
        ///
        /// <para>Colour is deliberately not in here. Every theme paints the same tokens;
        /// this is only for what a palette cannot say - a different template, a different
        /// shape, settings that live one level down.</para>
        /// </remarks>
        private static readonly Uri ArsenalChromeUri =
            new("pack://application:,,,/Arsenal;component/Styles/ArsenalTheme.xaml");

        private static ResourceDictionary? _themeChrome;

        private static void ApplyThemeChrome(Theming.ThemePalette palette)
        {
            bool wanted = ReferenceEquals(palette, Theming.ThemePalette.Arsenal);

            if (!wanted)
            {
                if (_themeChrome is not null)
                {
                    Current.Resources.MergedDictionaries.Remove(_themeChrome);
                    _themeChrome = null;
                }
                return;
            }

            if (_themeChrome is not null)
            {
                // Already on, but another dictionary may have been merged after it.
                // Move it back to the end so its templates still win.
                Current.Resources.MergedDictionaries.Remove(_themeChrome);
                Current.Resources.MergedDictionaries.Add(_themeChrome);
                return;
            }

            _themeChrome = new ResourceDictionary { Source = ArsenalChromeUri };
            Current.Resources.MergedDictionaries.Add(_themeChrome);
        }

        private static void ApplyShapeResources(Theming.ThemePalette palette)
        {
            Current.Resources["RadiusControl"] = new CornerRadius(palette.RadiusControl);
            Current.Resources["RadiusCard"] = new CornerRadius(palette.RadiusCard);
            Current.Resources["RadiusOverlay"] = new CornerRadius(palette.RadiusOverlay);
            Current.Resources["RadiusFlyout"] = new CornerRadius(palette.RadiusFlyout);
            Current.Resources["BorderWeight"] = new Thickness(palette.BorderWeight);
        }

        /// <summary>
        /// The two type families and the treatment headings get.
        /// </summary>
        private static void ApplyTypeResources(Theming.ThemePalette palette)
        {
            Current.Resources["FontBody"] = new System.Windows.Media.FontFamily(palette.BodyFont);
            Current.Resources["FontDisplay"] = new System.Windows.Media.FontFamily(palette.DisplayFont);
        }

        /// <summary>
        /// The library's own foregrounds for a navigation destination, in the order the
        /// template reaches for them. Every state resolves to the same ink.
        /// </summary>
        private static readonly string[] NavigationForegroundKeys =
        {
            "NavigationViewItemForeground",
            "NavigationViewItemForegroundPointerOver",
            "NavigationViewItemForegroundPressed",
            "NavigationViewItemForegroundLeftFluent",
            "NavigationViewItemForegroundPointerOverLeftFluent",
        };

        /// <summary>
        /// Paints the navigation column's ink: the destinations in the pane, and the
        /// title-bar icons that sit in the same gutter.
        /// </summary>
        /// <remarks>
        /// Written straight onto the application dictionary, and after the library's
        /// theme has been applied. ApplicationThemeManager.Apply appends its own
        /// dictionary last, so the same key merged in from one of ours would be the
        /// losing entry - the same reason NavigationViewContentBackground is set from
        /// here rather than declared in DesignTokens.xaml.
        ///
        /// One colour in every state. A selected destination is already marked by its
        /// pill and its indicator, so recolouring its text as well would say the same
        /// thing twice; hover and press keep their wash behind the item.
        ///
        /// One ink per theme. The warm grey is the dark theme's, and it was written
        /// into both: on the light pane it is a pale grey on a pale ground, which is
        /// how the destinations and the title-bar icons came to be barely legible
        /// there. Light mode gets the same hue turned dark instead - the warmth is the
        /// part that was wanted, not the lightness.
        ///
        /// How dark is not a taste call: the light ink is the one that reads against
        /// its pane the way the dark ink reads against its own. #B1B1A9 on the dark
        /// pane is 8.75:1, and #45453D on the light pane is 8.72:1, so the column
        /// carries the same weight relative to the page in both themes. Landing it at
        /// the secondary ink instead, which is where a quiet column would sit, measured
        /// 6.17:1 and still read as washed out next to #1A1A1A page text.
        /// </remarks>
        private static void ApplyNavigationForeground(Theming.ThemePalette palette)
        {
            if (Current is null) return;

            SolidColorBrush ink = Brush(palette.NavigationInk);
            Current.Resources["NavigationForeground"] = ink;
            foreach (string key in NavigationForegroundKeys)
                Current.Resources[key] = ink;
        }

        private static void ApplyWindowSurfaces(Theming.ThemePalette palette)
        {
            if (Current is null) return;

            Current.Resources["AppSidebarBackground"] = Brush(palette.OpaqueSidebar);
            Current.Resources["AppContentBackground"] = Brush(palette.OpaqueContent);

            // The NavigationView template lays its own wash over the whole content area -
            // NavigationViewContentBackground, a 30% #3A3A3A. It sits above the grounds
            // above, so the content area rendered #202020 instead of #151515, and the
            // title-bar strip #1D1D1D instead of the navigation colour. We paint that
            // ground ourselves, so the control's own is removed rather than fought with.
            // Set on the application dictionary, which is what the template's lookup
            // actually reaches; a value merged in from a child dictionary does not win.
            Current.Resources["NavigationViewContentBackground"] = System.Windows.Media.Brushes.Transparent;

            if (Current.MainWindow is Wpf.Ui.Controls.FluentWindow window)
                ApplyWindowSurface(window);
        }

        /// <summary>
        /// Applies the solid application ground and matching DWM frame to one window.
        /// </summary>
        /// <remarks>
        /// Called from the window's own constructor as well as from a theme refresh so
        /// windows created after startup receive the currently selected palette.
        /// </remarks>
        public static void ApplyWindowSurface(Wpf.Ui.Controls.FluentWindow window)
        {
            window.WindowBackdropType = Wpf.Ui.Controls.WindowBackdropType.None;

            window.Background = Current?.Resources["AppContentBackground"] as System.Windows.Media.Brush
                ?? System.Windows.Media.Brushes.Black;

            ApplyCompositionGround(window);
            ApplyImmersiveDarkMode(window);
        }

        /// <summary>
        /// Tells DWM whether this window is a dark one.
        /// </summary>
        /// <remarks>
        /// DWM draws the frame around the client area itself, including the strip it
        /// exposes while a window is being resized, and it picks those colours from this
        /// flag rather than from anything the application paints. Left unset the window
        /// is treated as light, so that strip is drawn light - the white edge that
        /// appears on whichever side is being dragged.
        ///
        /// Set explicitly because there is no backdrop operation left to set it as a
        /// side effect. The native frame must match the selected solid light or dark
        /// surface from the first window onward.
        /// </remarks>
        private static void ApplyImmersiveDarkMode(Window window)
        {
            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;

            int dark = IsConfiguredLightTheme() ? 0 : 1;
            DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));
        }

        private const int DwmwaUseImmersiveDarkMode = 20;

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        /// <summary>
        /// The colour WPF clears this window's composition target to, which is what a
        /// resize uncovers before anything has been drawn into the new space.
        /// </summary>
        /// <remarks>
        /// WPF's default here is opaque white. Leaving it untouched would make a window
        /// edge flash white while it is dragged outward, until the next frame catches up.
        ///
        /// The content ground is the right colour to use: the sidebar's differs by four
        /// values, which is not visible, and the content is by far the larger area.
        ///
        /// Note this is the render target's own clear colour, not a paint. Content draws
        /// over it exactly as before, so nothing is drawn twice. Filling the client area
        /// through GDI to achieve the same thing does double-paint, and flickers.
        /// </remarks>
        private static void ApplyCompositionGround(Window window)
        {
            // Only exists once the window has a handle, so this is a no-op when called
            // from a constructor; the window re-applies it from OnSourceInitialized.
            if (PresentationSource.FromVisual(window) is not HwndSource source) return;
            if (source.CompositionTarget is not { } target) return;

            target.BackgroundColor =
                (Current?.Resources["AppContentBackground"] as System.Windows.Media.SolidColorBrush)?.Color
                ?? System.Windows.Media.Colors.Black;
        }

        /// <summary>
        /// Refreshes only accent resources. The Settings colour picker uses this path
        /// while dragging so it does not reload the full light/dark theme on every move.
        /// </summary>
        public static void ApplyConfiguredAccent()
        {
            try
            {
                if (Current != null) ApplyAccentResources(ConfiguredPalette());
            }
            catch (Exception ex)
            {
                Logger.WriteLine("Accent apply error: " + ex.Message);
            }
        }

        /// <summary>The palette the stored setting asks for, System resolved against Windows.</summary>
        private static Theming.ThemePalette ConfiguredPalette()
        {
            bool systemLight = Convert.ToInt32(Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme", 0)) != 0;
            return Theming.ThemePalette.Resolve(AppConfig.Get("theme", 0), systemLight);
        }

        private static bool IsConfiguredLightTheme() => ConfiguredPalette().IsLight;

        private static void ApplyAccentResources(Theming.ThemePalette palette)
        {
            bool light = palette.IsLight;

            // A designed theme brings its own signal colour and ignores the desktop's.
            // The neutral two are backdrops for whatever the user's Windows accent is;
            // this one is a designed object, and an arbitrary accent against that ground
            // reads as a mistake rather than a preference.
            System.Windows.Media.Color configuredAccent = palette.SignatureAccent is string signature
                ? (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(signature)
                : AccentColorService.GetConfiguredAccent();
            bool followsWindows = palette.SignatureAccent is null && AccentColorService.UsesWindowsAccent;
            WpfUi.ApplicationTheme applicationTheme = light ? WpfUi.ApplicationTheme.Light : WpfUi.ApplicationTheme.Dark;

            // WPF UI owns the native-looking controls; Arsenal owns the surrounding
            // cards, tiles and charts. Apply once to WPF UI, then reuse its most-used
            // derived shade so the two layers never drift into different accent hues.
            WpfUi.ApplicationAccentColorManager.Apply(
                configuredAccent,
                applicationTheme,
                systemGlassColor: followsWindows,
                systemAccentColor: followsWindows);

            System.Windows.Media.Color accent = WpfUi.ApplicationAccentColorManager.SecondaryAccent;
            if (accent.A == 0)
                accent = configuredAccent;
            else
                accent = System.Windows.Media.Color.FromRgb(accent.R, accent.G, accent.B);

            // The ground the selected-row wash is mixed against, so the blend lands on
            // the card the user is actually looking at rather than on a remembered one.
            var card = (System.Windows.Media.Color)
                System.Windows.Media.ColorConverter.ConvertFromString(palette.AccentBlendGround);
            System.Windows.Media.Color foreground = AccentColorService.ContrastingText(accent);
            System.Windows.Media.Color subtle = AccentColorService.WithAlpha(accent, light ? (byte)0x20 : (byte)0x2E);
            System.Windows.Media.Color selected = AccentColorService.Blend(card, accent, light ? 0.14 : 0.20);

            Current.Resources["AccentPrimaryColor"] = accent;
            Current.Resources["AccentForegroundColor"] = foreground;
            Current.Resources["AccentSubtleColor"] = subtle;
            Current.Resources["SurfaceSelectedColor"] = selected;
            Current.Resources["AccentPrimary"] = Brush(accent);
            Current.Resources["AccentForeground"] = Brush(foreground);
            Current.Resources["AccentSubtle"] = Brush(subtle);
            Current.Resources["SurfaceSelected"] = Brush(selected);

            ApplySignalResources(palette, accent);
        }

        /// <summary>
        /// The glow behind a selected control.
        /// </summary>
        /// <remarks>
        /// Built here rather than declared in XAML because it takes the live accent,
        /// which is only known once <see cref="ApplyAccentResources"/> has run, and
        /// because a theme that does not glow wants the resource absent rather than an
        /// invisible effect still costing a render pass. A null Effect is free.
        /// </remarks>
        private static void ApplySignalResources(Theming.ThemePalette palette, System.Windows.Media.Color accent)
        {
            Current.Resources["AccentGlow"] = palette.Glows
                ? Frozen(new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = accent,
                    BlurRadius = 14,
                    ShadowDepth = 0,
                    Opacity = 0.55,
                })
                : null;
        }

        private static T Frozen<T>(T freezable) where T : System.Windows.Freezable
        {
            freezable.Freeze();
            return freezable;
        }

        private static SolidColorBrush Brush(string hex)
        {
            var brush = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            return brush;
        }

        private static SolidColorBrush Brush(System.Windows.Media.Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        /// <summary>
        /// A hairline that fades out at both ends, used to separate settings rows.
        /// </summary>
        private static System.Windows.Media.LinearGradientBrush FadedLine(string hex)
        {
            var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
            var transparent = System.Windows.Media.Color.FromArgb(0, color.R, color.G, color.B);

            var brush = new System.Windows.Media.LinearGradientBrush
            {
                StartPoint = new System.Windows.Point(0, 0),
                EndPoint = new System.Windows.Point(1, 0)
            };
            brush.GradientStops.Add(new System.Windows.Media.GradientStop(transparent, 0));
            brush.GradientStops.Add(new System.Windows.Media.GradientStop(color, 0.06));
            brush.GradientStops.Add(new System.Windows.Media.GradientStop(color, 0.94));
            brush.GradientStops.Add(new System.Windows.Media.GradientStop(transparent, 1));
            brush.Freeze();
            return brush;
        }
    }
}
