using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace SRFlasher.Utilities
{
    /// <summary>
    /// A8: the single gate and the single vocabulary for every hand-written
    /// motion in the app. The app language is 150-200 ms ease-out, red-accent
    /// animations; anything motion-driven from code must go through here so
    /// the user's own "animate controls" Windows setting is honoured.
    /// (Style-trigger animations in XAML are sub-200 ms pulses that only run
    /// while flashing; they stay declarative.)
    /// </summary>
    public static class Motion
    {
        /// <summary>Windows' "Animate controls and elements inside windows" setting,
        /// AND the app's own Reduce-motion toggle on the settings page.</summary>
        public static bool ReduceMotion { get; set; }

        public static bool Enabled => SystemParameters.ClientAreaAnimation && !ReduceMotion;

        public static TimeSpan Fast => TimeSpan.FromMilliseconds(120);
        public static TimeSpan Base => TimeSpan.FromMilliseconds(160);

        private static readonly IEasingFunction EaseOut = new QuadraticEase { EasingMode = EasingMode.EaseOut };

        /// <summary>
        /// A1/A3/A4 entrance: fade in while sliding toward rest. The offsets
        /// decide the direction: fromY slides up from below (cards), a
        /// negative fromX slides in from the left — the tab pages' standard
        /// left-to-right arrival. No-op when motion is disabled.
        /// </summary>
        public static void FadeSlideIn(FrameworkElement element, double fromY = 12, double fromX = 0)
        {
            if (element == null || !Enabled) return;

            var translate = element.RenderTransform as TranslateTransform;
            if (translate == null)
            {
                translate = new TranslateTransform();
                element.RenderTransform = translate;
            }
            translate.X = fromX;
            translate.Y = fromY;

            var fade = new DoubleAnimation(0, 1, Base) { EasingFunction = EaseOut };
            element.BeginAnimation(UIElement.OpacityProperty, fade);
            if (fromY != 0)
                translate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(fromY, 0, Base) { EasingFunction = EaseOut });
            if (fromX != 0)
                translate.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(fromX, 0, Base) { EasingFunction = EaseOut });
        }

        /// <summary>A7 completion pop: 1 -> 1.18 -> 1 scale pulse.</summary>
        public static void PopScale(FrameworkElement element)
        {
            if (element == null || !Enabled) return;

            var scale = element.RenderTransform as ScaleTransform;
            if (scale == null)
            {
                scale = new ScaleTransform(1, 1);
                element.RenderTransformOrigin = new Point(0.5, 0.5);
                element.RenderTransform = scale;
            }
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, PopPulse());
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, PopPulse());
        }

        private static DoubleAnimationUsingKeyFrames PopPulse()
        {
            // Up-swing then settle, both keyed off the rest position.
            var pulse = new DoubleAnimationUsingKeyFrames();
            pulse.KeyFrames.Add(new LinearDoubleKeyFrame(1.18, KeyTime.FromTimeSpan(Fast)));
            pulse.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(Fast + Base)));
            return pulse;
        }

        /// <summary>A4: element fades/slides in every time it becomes visible.</summary>
        public static readonly DependencyProperty FadeInOnShowProperty = DependencyProperty.RegisterAttached(
            "FadeInOnShow", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnFadeInOnShowChanged));

        public static bool GetFadeInOnShow(DependencyObject obj) => (bool)obj.GetValue(FadeInOnShowProperty);
        public static void SetFadeInOnShow(DependencyObject obj, bool value) => obj.SetValue(FadeInOnShowProperty, value);

        private static void OnFadeInOnShowChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (!(d is UIElement element)) return;

            if ((bool)e.NewValue)
                element.IsVisibleChanged += FadeInOnVisible;
            else
                element.IsVisibleChanged -= FadeInOnVisible;
        }

        private static void FadeInOnVisible(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (sender is FrameworkElement fe && (bool)e.NewValue)
                FadeSlideIn(fe);
        }

        /// <summary>
        /// Side-banner (toast) entrance: fade in while sliding in from the
        /// right edge, settling with a slight overshoot so the banner feels
        /// like it docks into place. No-op (rest state) when motion is off.
        /// </summary>
        public static void ToastIn(FrameworkElement element)
        {
            if (element == null || !Enabled) return;

            var translate = element.RenderTransform as TranslateTransform;
            if (translate == null)
            {
                translate = new TranslateTransform();
                element.RenderTransform = translate;
            }
            translate.X = 48;

            var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = EaseOut };
            var slide = new DoubleAnimation(48, 0, TimeSpan.FromMilliseconds(320))
            {
                EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 }
            };
            element.BeginAnimation(UIElement.OpacityProperty, fade);
            translate.BeginAnimation(TranslateTransform.XProperty, slide);
        }

        /// <summary>
        /// Side-banner (toast) exit: slide back out to the right edge while
        /// fading, instead of vanishing instantly. The caller removes the
        /// item once the exit has had time to play (or immediately when
        /// motion is off).
        /// </summary>
        public static void ToastOut(FrameworkElement element)
        {
            if (element == null || !Enabled) return;

            var translate = element.RenderTransform as TranslateTransform;
            if (translate == null)
            {
                translate = new TranslateTransform();
                element.RenderTransform = translate;
            }

            var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(160)) { EasingFunction = EaseOut };
            var slide = new DoubleAnimation(0, 56, TimeSpan.FromMilliseconds(200))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
            };
            element.BeginAnimation(UIElement.OpacityProperty, fade);
            translate.BeginAnimation(TranslateTransform.XProperty, slide);
        }
    }
}
