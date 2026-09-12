using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Arsenal.UI.Controls
{
    /// <summary>
    /// Shows one panel of a wizard at a time and animates the swap between them: the
    /// outgoing panel leaves against the direction of travel, the incoming one arrives
    /// from the opposite edge.
    ///
    /// The panels are ordinary <see cref="DataTemplate"/> resources looked up by
    /// <see cref="TemplateKeyPrefix"/> + step number. Swapping the template rather than
    /// the content is what lets every step keep the presenter's own DataContext - a
    /// content swap would re-root each panel's bindings on the step object instead.
    ///
    /// Animation is driven from code rather than from style triggers for the same reason
    /// as <see cref="OverlayHost"/>: a trigger starts a fresh clock on every step and
    /// never releases the previous one.
    /// </summary>
    public class StepPresenter : System.Windows.Controls.ContentControl
    {
        private const int FrameRate = 120;

        /// <summary>How far a panel slides, in device-independent pixels.</summary>
        private const double Travel = 34;

        private const int ExitMs = 130;
        private const int EnterMs = 300;

        private ContentPresenter? _host;
        private TranslateTransform? _translate;
        private ScaleTransform? _scale;

        /// <summary>The step whose template is mounted; -1 before the first one.</summary>
        private int _mounted = -1;

        private bool _isSwapping;
        private int? _queued;

        public static readonly DependencyProperty StepProperty =
            DependencyProperty.Register(nameof(Step), typeof(int), typeof(StepPresenter),
                new FrameworkPropertyMetadata(0, OnStepChanged));

        /// <summary>Index of the panel to show.</summary>
        public int Step
        {
            get => (int)GetValue(StepProperty);
            set => SetValue(StepProperty, value);
        }

        public static readonly DependencyProperty DirectionProperty =
            DependencyProperty.Register(nameof(Direction), typeof(int), typeof(StepPresenter),
                new PropertyMetadata(1));

        /// <summary>+1 when moving forward through the wizard, -1 when going back.</summary>
        public int Direction
        {
            get => (int)GetValue(DirectionProperty);
            set => SetValue(DirectionProperty, value);
        }

        public static readonly DependencyProperty TemplateKeyPrefixProperty =
            DependencyProperty.Register(nameof(TemplateKeyPrefix), typeof(string), typeof(StepPresenter),
                new PropertyMetadata("Step"));

        /// <summary>Resource key stem; the step number is appended to it.</summary>
        public string TemplateKeyPrefix
        {
            get => (string)GetValue(TemplateKeyPrefixProperty);
            set => SetValue(TemplateKeyPrefixProperty, value);
        }

        static StepPresenter()
        {
            DefaultStyleKeyProperty.OverrideMetadata(typeof(StepPresenter),
                new FrameworkPropertyMetadata(typeof(StepPresenter)));
        }

        public override void OnApplyTemplate()
        {
            base.OnApplyTemplate();

            _host = GetTemplateChild("PART_Host") as ContentPresenter;
            _translate = GetTemplateChild("PART_Translate") as TranslateTransform;
            _scale = GetTemplateChild("PART_Scale") as ScaleTransform;

            // The first panel is mounted without motion; sliding it in here would fight
            // the overlay's own opening animation.
            _mounted = -1;
            _isSwapping = false;
            _queued = null;
            Mount(Step);
            Settle();
        }

        private static void OnStepChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
            => ((StepPresenter)d).GoTo((int)e.NewValue);

        private void GoTo(int step)
        {
            if (_host is null) return;      // template not applied yet; OnApplyTemplate mounts it
            if (_mounted == step) return;

            // Coalesce: a click that lands mid-transition should end on the step the user
            // last asked for, not queue one animation per click.
            if (_isSwapping)
            {
                _queued = step;
                return;
            }

            _isSwapping = true;
            int direction = Direction >= 0 ? 1 : -1;

            _fade.Start(_host.Opacity, 0, ExitMs, FrameEase.CubicIn, v => _host.Opacity = v, completed: () =>
            {
                int target = _queued ?? step;
                _queued = null;

                Mount(target);
                _isSwapping = false;
                AnimateIn(direction);

                // A step asked for while this one was running still has to be honoured.
                if (_queued is int pending) GoTo(pending);
            });

            if (_translate is not null)
                _slide.Start(_translate.X, -Travel * direction * 0.55, ExitMs, FrameEase.CubicIn, v => _translate.X = v);
        }

        private void AnimateIn(int direction)
        {
            if (_host is null) return;

            if (_translate is not null)
            {
                _slide.Stop();
                _translate.X = Travel * direction;
                _slide.Start(_translate.X, 0, EnterMs, FrameEase.CubicOut, v => _translate.X = v);
            }

            if (_scale is not null)
            {
                _scaleXEase.Stop();
                _scaleYEase.Stop();
                _scale.ScaleX = _scale.ScaleY = 0.985;
                _scaleXEase.Start(0.985, 1, EnterMs, FrameEase.CubicOut, v => _scale.ScaleX = v);
                _scaleYEase.Start(0.985, 1, EnterMs, FrameEase.CubicOut, v => _scale.ScaleY = v);
            }

            // Opacity finishes ahead of the slide so the panel is readable while the last
            // few pixels of travel play out.
            _fade.Start(
                _host.Opacity, 1, EnterMs - 80, FrameEase.CubicOut,
                v => _host.Opacity = v,
                completed: Settle);
        }

        /// <summary>
        /// Drops every clock and writes the resting values back. A held animation stays
        /// active for the life of the control, and they accumulate across steps.
        /// </summary>
        private void Settle()
        {
            if (_isSwapping) return;

            _fade.Stop();
            _slide.Stop();
            _scaleXEase.Stop();
            _scaleYEase.Stop();

            if (_host is not null) _host.Opacity = 1;
            if (_translate is not null) _translate.X = 0;

            if (_scale is not null)
            {
                _scale.ScaleX = 1;
                _scale.ScaleY = 1;
            }
        }

        private readonly FrameEase _fade = new();
        private readonly FrameEase _slide = new();
        private readonly FrameEase _scaleXEase = new();
        private readonly FrameEase _scaleYEase = new();

        private void Mount(int step)
        {
            if (_mounted == step) return;
            _mounted = step;
            ContentTemplate = TryFindResource(TemplateKeyPrefix + step) as DataTemplate;
        }

    }
}
