using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Media;
using JiraTaskBar.Core.Jira;

namespace JiraTaskBar.App.Views;

public sealed class DueDateIndicator : FrameworkElement
{
    private static readonly Color Red = Color.FromRgb(211, 41, 54);
    private static readonly Color Amber = Color.FromRgb(219, 165, 32);
    private static readonly Color Green = Color.FromRgb(22, 132, 71);
    private static readonly Brush UsedTimeFill = Freeze(new SolidColorBrush(Color.FromArgb(65, 128, 128, 128)));
    private static readonly Pen NeutralOutline = Freeze(new Pen(Brushes.Gray, 1.2));
    private static readonly Pen BreachOutline = Freeze(new Pen(Freeze(new SolidColorBrush(Red)), 1.6));
    private static readonly Pen Checkmark = Freeze(new Pen(Brushes.White, 1.3));
    private static readonly Pen[] ProgressPens = Enumerable.Range(0, 101).Select(percent =>
    {
        var color = percent <= 50 ? Mix(Red, Amber, percent / 50d) : Mix(Amber, Green, (percent - 50) / 50d);
        return Freeze(new Pen(Freeze(new SolidColorBrush(color)), 1.2));
    }).ToArray();

    public static readonly DependencyProperty StateProperty = DependencyProperty.Register(nameof(State),
        typeof(DueDateState), typeof(DueDateIndicator), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public DueDateState? State { get => (DueDateState?)GetValue(StateProperty); set => SetValue(StateProperty, value); }

    protected override AutomationPeer OnCreateAutomationPeer() => new FrameworkElementAutomationPeer(this);

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        var state = State;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var radius = Math.Max(0, Math.Min(ActualWidth, ActualHeight) / 2 - 1);
        if (radius == 0 || state is null || state.Kind == DueDateKind.None) return;
        if (state.Kind == DueDateKind.Overdue)
        {
            dc.DrawEllipse(Brushes.Black, BreachOutline, center, radius, radius);
            return;
        }

        if (state.Kind == DueDateKind.Met)
        {
            dc.DrawEllipse(ProgressPens[^1].Brush, null, center, radius, radius);
            var corner = new Point(center.X - 0.12 * radius, center.Y + 0.25 * radius);
            dc.DrawLine(Checkmark, new Point(center.X - 0.42 * radius, center.Y), corner);
            dc.DrawLine(Checkmark, corner, new Point(center.X + 0.43 * radius, center.Y - 0.35 * radius));
            return;
        }

        if (state.RemainingFraction is not { } value || !double.IsFinite(value))
        {
            dc.DrawEllipse(null, NeutralOutline, center, radius, radius);
            return;
        }
        var fraction = Math.Clamp(value, 0, 1);
        var pen = ProgressPens[(int)Math.Round(fraction * 100)];
        if (fraction >= 1)
        {
            dc.DrawEllipse(pen.Brush, pen, center, radius, radius);
            return;
        }

        dc.DrawEllipse(UsedTimeFill, null, center, radius, radius);
        if (fraction > 0)
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
            dc.DrawGeometry(pen.Brush, null, geometry);
        }
        dc.DrawEllipse(null, pen, center, radius, radius);
    }

    private static Color Mix(Color from, Color to, double progress) => Color.FromRgb(
        (byte)Math.Round(from.R + (to.R - from.R) * progress),
        (byte)Math.Round(from.G + (to.G - from.G) * progress),
        (byte)Math.Round(from.B + (to.B - from.B) * progress));

    private static T Freeze<T>(T value) where T : Freezable
    {
        value.Freeze();
        return value;
    }
}
