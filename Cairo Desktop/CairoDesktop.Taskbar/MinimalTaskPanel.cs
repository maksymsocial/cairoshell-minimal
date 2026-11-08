using System;
using System.Windows;
using System.Windows.Controls;

namespace CairoDesktop.Taskbar
{
    /// <summary>
    /// Lays task buttons out left to right, each as wide as MaxItemWidth,
    /// shrinking them all evenly once they no longer fit (xfce/fvwm style).
    /// </summary>
    public class MinimalTaskPanel : Panel
    {
        public static readonly DependencyProperty MaxItemWidthProperty = DependencyProperty.Register(
            nameof(MaxItemWidth), typeof(double), typeof(MinimalTaskPanel),
            new FrameworkPropertyMetadata(200.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

        public double MaxItemWidth
        {
            get => (double)GetValue(MaxItemWidthProperty);
            set => SetValue(MaxItemWidthProperty, value);
        }

        private double itemWidth(double available)
        {
            int count = 0;
            foreach (UIElement child in InternalChildren)
            {
                if (child.Visibility != Visibility.Collapsed) count++;
            }

            if (count == 0) return 0;
            if (double.IsInfinity(available)) return MaxItemWidth;

            return Math.Max(24, Math.Min(MaxItemWidth, Math.Floor(available / count)));
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            double w = itemWidth(availableSize.Width);
            double h = double.IsInfinity(availableSize.Height) ? 0 : availableSize.Height;
            double total = 0;

            foreach (UIElement child in InternalChildren)
            {
                if (child.Visibility == Visibility.Collapsed) continue;
                child.Measure(new Size(w, availableSize.Height));
                total += w;
                h = Math.Max(h, child.DesiredSize.Height);
            }

            return new Size(double.IsInfinity(availableSize.Width) ? total : Math.Min(total, availableSize.Width), h);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            double w = itemWidth(finalSize.Width);
            double x = 0;

            // when the buttons are squeezed, hand the leftover pixels out one by one
            // so the last button ends exactly at the tray instead of leaving a gap
            int count = 0;
            foreach (UIElement child in InternalChildren)
            {
                if (child.Visibility != Visibility.Collapsed) count++;
            }

            int extra = 0;
            if (count > 0 && w < MaxItemWidth && !double.IsInfinity(finalSize.Width))
            {
                extra = Math.Max(0, (int)Math.Floor(finalSize.Width - w * count));
            }

            foreach (UIElement child in InternalChildren)
            {
                if (child.Visibility == Visibility.Collapsed) continue;
                double cw = w + (extra > 0 ? 1 : 0);
                if (extra > 0) extra--;
                child.Arrange(new Rect(x, 0, cw, finalSize.Height));
                x += cw;
            }

            return finalSize;
        }
    }
}
