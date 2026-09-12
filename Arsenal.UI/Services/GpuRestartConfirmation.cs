using Arsenal.Helpers;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using MediaBrush = System.Windows.Media.Brush;
using MediaBrushes = System.Windows.Media.Brushes;
using Ui = Wpf.Ui.Controls;

namespace Arsenal.UI.Services;

internal static class GpuRestartConfirmation
{
    public static bool Show(Window? owner, bool enablingUltimate)
    {
        bool accepted = false;
        var dialog = new Window
        {
            Title = AppStrings.Get("GpuRestartConfirmationRestartRequired"),
            Width = 430,
            // Deliberately not SizeToContent: on a WindowStyle.None + AllowsTransparency
            // window it measures before the body text has wrapped at the real width and
            // then keeps that first, too-tall result. The height is driven from the
            // content's own arranged height below instead.
            SizeToContent = SizeToContent.Manual,
            Height = 240,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            AllowsTransparency = true,
            Background = MediaBrushes.Transparent,
            WindowStartupLocation = owner?.IsVisible == true ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            ShowInTaskbar = false,
            Topmost = owner?.IsVisible != true
        };
        if (owner?.IsVisible == true) dialog.Owner = owner;

        var root = new Border
        {
            Background = (MediaBrush)System.Windows.Application.Current.FindResource("SurfaceLayer"),
            BorderBrush = (MediaBrush)System.Windows.Application.Current.FindResource("StrokeSubtle"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(22),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 34, ShadowDepth = 8, Opacity = .42 }
        };
        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var heading = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        heading.Children.Add(new Ui.SymbolIcon { Symbol = Ui.SymbolRegular.Warning24, FontSize = 24, Foreground = (MediaBrush)System.Windows.Application.Current.FindResource("StatusWarning"), Margin = new Thickness(0, 0, 12, 0) });
        heading.Children.Add(new System.Windows.Controls.TextBlock { Text = AppStrings.Get("GpuRestartConfirmationRestartRequired"), FontSize = 20, FontWeight = FontWeights.SemiBold, Foreground = (MediaBrush)System.Windows.Application.Current.FindResource("TextPrimary"), VerticalAlignment = VerticalAlignment.Center });
        layout.Children.Add(heading);

        var body = new System.Windows.Controls.TextBlock
        {
            Text = enablingUltimate
                ? AppStrings.Get("GpuRestartConfirmationSwitchingToUltimateConnectsThe")
                : AppStrings.Get("GpuRestartConfirmationLeavingUltimateReconnectsThe"),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 14,
            LineHeight = 21,
            Foreground = (MediaBrush)System.Windows.Application.Current.FindResource("TextSecondary"),
            // Full width, not indented to the title. Starting it past the warning icon
            // left a dead column under the icon and squeezed the body into a narrower
            // measure than the dialog, so it wrapped early with nothing beside it.
            Margin = new Thickness(0, 18, 0, 18)
        };
        Grid.SetRow(body, 1);
        layout.Children.Add(body);

        var actions = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        var cancel = new Ui.Button { Content = AppStrings.Get("GpuRestartConfirmationCancel"), Appearance = Ui.ControlAppearance.Secondary, MinWidth = 92, Margin = new Thickness(0, 0, 8, 0) };
        var confirm = new Ui.Button { Content = AppStrings.Get("GpuRestartConfirmationRestartAndApply"), Appearance = Ui.ControlAppearance.Primary, MinWidth = 132 };
        cancel.Click += (_, _) => dialog.Close();
        confirm.Click += (_, _) => { accepted = true; dialog.Close(); };
        actions.Children.Add(cancel);
        actions.Children.Add(confirm);
        Grid.SetRow(actions, 2);
        layout.Children.Add(actions);

        root.Child = layout;
        dialog.Content = root;

        // Top-aligned, so the Border takes its natural content height instead of
        // stretching to whatever the window happens to be. Its arranged height is then
        // the exact height the window should have, and SizeChanged reports it after
        // every real layout pass - no manual Measure, no guessing.
        root.VerticalAlignment = VerticalAlignment.Top;
        root.SizeChanged += (_, e) =>
        {
            double target = Math.Ceiling(e.NewSize.Height);
            if (target > 0 && Math.Abs(dialog.Height - target) > 0.5) dialog.Height = target;
        };

        var scale = new ScaleTransform(0.94, 0.94);
        root.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
        root.RenderTransform = scale;
        dialog.Opacity = 0;

        // ContentRendered, not Loaded: by then the height correction above has settled,
        // so the dialog does not visibly resize as it fades in.
        dialog.ContentRendered += (_, _) => AnimateIn(dialog, scale);

        bool closingAnimationDone = false;
        dialog.Closing += (_, e) =>
        {
            if (closingAnimationDone) return;
            e.Cancel = true;
            closingAnimationDone = true;
            AnimateOut(dialog, scale, dialog.Close);
        };

        dialog.ShowDialog();
        return accepted;
    }

    private static readonly Arsenal.UI.Controls.FrameEase DialogFade = new();
    private static readonly Arsenal.UI.Controls.FrameEase DialogScaleX = new();
    private static readonly Arsenal.UI.Controls.FrameEase DialogScaleY = new();

    private static void AnimateIn(Window dialog, ScaleTransform scale)
    {
        DialogFade.Start(dialog.Opacity, 1, 150, Arsenal.UI.Controls.FrameEase.CubicOut, v => dialog.Opacity = v);
        DialogScaleX.Start(scale.ScaleX, 1, 220, Arsenal.UI.Controls.FrameEase.CubicOut, v => scale.ScaleX = v);
        DialogScaleY.Start(scale.ScaleY, 1, 220, Arsenal.UI.Controls.FrameEase.CubicOut, v => scale.ScaleY = v);
    }

    private static void AnimateOut(Window dialog, ScaleTransform scale, Action done)
    {
        DialogScaleX.Start(scale.ScaleX, 0.96, 120, Arsenal.UI.Controls.FrameEase.CubicIn, v => scale.ScaleX = v);
        DialogScaleY.Start(scale.ScaleY, 0.96, 120, Arsenal.UI.Controls.FrameEase.CubicIn, v => scale.ScaleY = v);
        DialogFade.Start(dialog.Opacity, 0, 120, Arsenal.UI.Controls.FrameEase.CubicIn, v => dialog.Opacity = v, completed: done);
    }
}
