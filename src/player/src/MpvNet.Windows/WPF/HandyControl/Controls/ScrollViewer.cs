using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

using HandyControl.Data;

namespace HandyControl.Controls
{
    public class ScrollViewer : System.Windows.Controls.ScrollViewer
    {
        private double? _verticalWheelOffset;

        private double? _horizontalWheelOffset;

        public static readonly DependencyProperty OrientationProperty = DependencyProperty.Register(
            "Orientation", typeof(Orientation), typeof(ScrollViewer), new PropertyMetadata(Orientation.Vertical));

        public Orientation Orientation
        {
            get => (Orientation) GetValue(OrientationProperty);
            set => SetValue(OrientationProperty, value);
        }

        public static readonly DependencyProperty CanMouseWheelProperty = DependencyProperty.Register(
            "CanMouseWheel", typeof(bool), typeof(ScrollViewer), new PropertyMetadata(ValueBoxes.TrueBox));

        public bool CanMouseWheel
        {
            get => (bool) GetValue(CanMouseWheelProperty);
            set => SetValue(CanMouseWheelProperty, ValueBoxes.BooleanBox(value));
        }

        public static readonly DependencyProperty IsPixelScrollingEnabledProperty = DependencyProperty.Register(
            "IsPixelScrollingEnabled", typeof(bool), typeof(ScrollViewer), new PropertyMetadata(ValueBoxes.FalseBox));

        public bool IsPixelScrollingEnabled
        {
            get => (bool) GetValue(IsPixelScrollingEnabledProperty);
            set => SetValue(IsPixelScrollingEnabledProperty, ValueBoxes.BooleanBox(value));
        }

        protected override void OnMouseWheel(MouseWheelEventArgs e)
        {
            if (!CanMouseWheel || e.Handled || e.Delta == 0) return;

            if (ScrollInfo == null) return;

            if (IsPixelScrollingEnabled)
            {
                // Accumulate input before the next layout. ScrollInfo owns realization
                // and positioning; wheel input needs no animation or command queue.
                if (Orientation == Orientation.Vertical)
                {
                    _verticalWheelOffset = Math.Clamp((_verticalWheelOffset ?? ScrollInfo.VerticalOffset) - e.Delta,
                        0, Math.Max(0, ScrollInfo.ExtentHeight - ScrollInfo.ViewportHeight));
                    ScrollInfo.SetVerticalOffset(_verticalWheelOffset.Value);
                }
                else
                {
                    _horizontalWheelOffset = Math.Clamp((_horizontalWheelOffset ?? ScrollInfo.HorizontalOffset) - e.Delta,
                        0, Math.Max(0, ScrollInfo.ExtentWidth - ScrollInfo.ViewportWidth));
                    ScrollInfo.SetHorizontalOffset(_horizontalWheelOffset.Value);
                }
            }
            else if (Orientation == Orientation.Vertical)
            {
                base.OnMouseWheel(e);
            }
            else if (e.Delta < 0)
            {
                ScrollInfo.MouseWheelRight();
            }
            else
            {
                ScrollInfo.MouseWheelLeft();
            }

            e.Handled = true;
        }

        protected override void OnScrollChanged(ScrollChangedEventArgs e)
        {
            if (e.OriginalSource == this)
            {
                _verticalWheelOffset = null;
                _horizontalWheelOffset = null;
            }
            base.OnScrollChanged(e);
        }

        protected override void OnManipulationStarting(ManipulationStartingEventArgs e)
        {
            _verticalWheelOffset = null;
            _horizontalWheelOffset = null;
            base.OnManipulationStarting(e);
        }

        protected override HitTestResult HitTestCore(PointHitTestParameters hitTestParameters) =>
            IsPenetrating ? null : base.HitTestCore(hitTestParameters);

        public static readonly DependencyProperty IsPenetratingProperty = DependencyProperty.RegisterAttached(
            "IsPenetrating", typeof(bool), typeof(ScrollViewer), new PropertyMetadata(ValueBoxes.FalseBox));

        public bool IsPenetrating
        {
            get => (bool) GetValue(IsPenetratingProperty);
            set => SetValue(IsPenetratingProperty, ValueBoxes.BooleanBox(value));
        }

        public static void SetIsPenetrating(DependencyObject element, bool value) => element.SetValue(IsPenetratingProperty, ValueBoxes.BooleanBox(value));

        public static bool GetIsPenetrating(DependencyObject element) => (bool) element.GetValue(IsPenetratingProperty);
    }
}
