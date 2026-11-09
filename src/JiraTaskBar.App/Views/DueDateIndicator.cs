using System.Globalization;
using System.Windows;
using System.Windows.Media;
using JiraTaskBar.Core.Jira;

namespace JiraTaskBar.App.Views;

public sealed class DueDateIndicator : FrameworkElement
{
    public static readonly DependencyProperty StateProperty = DependencyProperty.Register(nameof(State),
        typeof(DueDateState), typeof(DueDateIndicator), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public DueDateState? State { get => (DueDateState?)GetValue(StateProperty); set => SetValue(StateProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        var state = State;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var radius = Math.Max(0, Math.Min(ActualWidth, ActualHeight) / 2 - 1);
        if (radius == 0) return;
        var brush = state?.Kind switch { DueDateKind.Overdue => Brushes.Firebrick, DueDateKind.Today => Brushes.DarkOrange,
            DueDateKind.Soon => Brushes.Goldenrod, DueDateKind.Upcoming => Brushes.SteelBlue, _ => Brushes.Gray };
        dc.DrawEllipse(null, new Pen(brush, 1.2), center, radius, radius);
        if (state?.RemainingFraction is > 0 and < 1 and var fraction)
        {
            var angle = fraction * Math.PI * 2;
            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                ctx.BeginFigure(center, true, true);
                ctx.LineTo(new Point(center.X, center.Y - radius), true, false);
                ctx.ArcTo(new Point(center.X + radius * Math.Sin(angle), center.Y - radius * Math.Cos(angle)),
                    new Size(radius, radius), 0, fraction > 0.5, SweepDirection.Clockwise, true, false);
            }
            dc.DrawGeometry(brush, null, geometry);
        }
        else if (state?.RemainingFraction >= 1) dc.DrawEllipse(brush, null, center, radius, radius);
        var symbol = state?.Kind switch { DueDateKind.Overdue => "!", DueDateKind.Today => "T", DueDateKind.Soon => "S", DueDateKind.None => "–", _ => "" };
        if (symbol.Length > 0)
        {
            var text = new FormattedText(symbol, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 10, Brushes.Black, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawRectangle(Brushes.White, null, new Rect(center.X - text.Width / 2, center.Y - text.Height / 2, text.Width, text.Height));
            dc.DrawText(text, new Point(center.X - text.Width / 2, center.Y - text.Height / 2));
        }
    }
}
