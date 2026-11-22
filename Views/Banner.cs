using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace pxdSpeak.Views;

public sealed record Notice(string Label, string Text, bool Warning = false, bool Sticky = false);

/// <summary>
/// The little pop-up in the bottom-right corner: "READING · Heart" while it reads (it stays until reading ends) and
/// short warnings. It never takes focus from the program in front; clicking it hides it.
/// </summary>
public static class Banner
{
    private static Window? _window;
    private static TextBlock _label = null!, _text = null!;
    private static Border _frame = null!, _badge = null!;
    private static DispatcherTimer? _timer;

    public static void Show(Notice n)
    {
        _window ??= Create();
        _label.Text = n.Label;
        _text.Text = n.Text;
        _frame.BorderBrush = new SolidColorBrush(Color.Parse(n.Warning ? "#7A2A33" : "#1D5A47"));
        _frame.BoxShadow = BoxShadows.Parse(n.Warning ? "0 0 30 0 #33F2666A" : "0 0 30 0 #3319F58C");
        _badge.Background = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.Parse(n.Warning ? "#FF7A6B" : "#00C8FF"), 0),
                new GradientStop(Color.Parse(n.Warning ? "#F2666A" : "#19F58C"), 1),
            },
        };
        if (!_window.IsVisible)
            _window.Show();
        Place();
        _timer!.Stop();
        if (!n.Sticky)
        {
            _timer.Interval = TimeSpan.FromSeconds(n.Warning ? 3.5 : 2.5);
            _timer.Start();
        }
    }

    public static void Hide()
    {
        _timer?.Stop();
        _window?.Close();
        _window = null;
    }

    private static Window Create()
    {
        _label = new TextBlock { Classes = { "label" } };
        _text = new TextBlock { FontSize = 15, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, MaxWidth = 300 };
        _badge = new Border
        {
            Width = 38, Height = 38, CornerRadius = new CornerRadius(10), VerticalAlignment = VerticalAlignment.Center,
            Child = new Viewbox { Margin = new Thickness(7), Child = BrandIcon.Art("#04140C") },
        };
        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(_label);
        text.Children.Add(_text);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        row.Children.Add(_badge);
        row.Children.Add(text);
        _frame = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#F2161922")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(14, 12, 18, 12),
            Child = row,
        };
        var window = new Window
        {
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowDecorations = WindowDecorations.None,
            Topmost = true,
            ShowInTaskbar = false,
            CanResize = false,
            ShowActivated = false,
            Background = Brushes.Transparent,
            TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent },
            Content = new Border { Padding = new Thickness(12), Child = _frame },
        };
        window.PointerPressed += (_, _) => Hide();
        window.SizeChanged += (_, _) => Place();
        _timer = new DispatcherTimer();
        _timer.Tick += (_, _) =>
        {
            if (window.IsPointerOver)
                return;
            Hide();
        };
        return window;
    }

    private static void Place()
    {
        if (_window?.Screens.Primary is not { } screen)
            return;
        var area = screen.WorkingArea;
        var scale = screen.Scaling;
        var w = (int)((_window.Bounds.Width > 0 ? _window.Bounds.Width : 340) * scale);
        var h = (int)((_window.Bounds.Height > 0 ? _window.Bounds.Height : 90) * scale);
        var gap = (int)(8 * scale);
        _window.Position = new PixelPoint(area.Right - w - gap, area.Bottom - h - gap);
    }
}
