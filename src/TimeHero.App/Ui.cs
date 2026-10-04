using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;

namespace TimeHero.App;

/// <summary>Palette (tema scuro) e piccoli costruttori di controlli: la UI è scritta in codice.</summary>
internal static class Ui
{
    public static SolidColorBrush Hex(string hex)
    {
        var b = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        b.Freeze();
        return b;
    }

    public static readonly FontFamily Font = new("Segoe UI Variable Text, Segoe UI");

    public static readonly Brush Bg = Hex(Theme.Bg);
    public static readonly Brush Surface = Hex(Theme.Surface);
    public static readonly Brush Surface2 = Hex(Theme.Surface2);
    public static readonly Brush RowAlt = Hex(Theme.RowAlt);
    public static readonly Brush Border = Hex(Theme.Border);
    public static readonly Brush Fg = Hex(Theme.Text);
    public static readonly Brush Subtle = Hex(Theme.Subtle);
    public static readonly Brush Accent = Hex(Theme.Accent);
    public static readonly Brush AccentDim = Hex(Theme.AccentDim);
    public static readonly Brush Danger = Hex(Theme.Danger);
    public static readonly Brush Go = Hex(Theme.Ok);

    private static readonly Style FlatButton = (Style)XamlReader.Parse(Theme.Tokenize("""
        <Style @NS@ TargetType="Button">
          <Setter Property="Template">
            <Setter.Value>
              <ControlTemplate TargetType="Button">
                <Border x:Name="b" Background="{TemplateBinding Background}" CornerRadius="8"
                        Padding="{TemplateBinding Padding}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="1">
                  <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
                </Border>
                <ControlTemplate.Triggers>
                  <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="b" Property="BorderBrush" Value="@Accent@"/></Trigger>
                  <Trigger Property="IsPressed" Value="True"><Setter TargetName="b" Property="Opacity" Value="0.75"/></Trigger>
                  <Trigger Property="IsEnabled" Value="False"><Setter TargetName="b" Property="Opacity" Value="0.35"/></Trigger>
                </ControlTemplate.Triggers>
              </ControlTemplate>
            </Setter.Value>
          </Setter>
        </Style>
        """));

    /// <summary>Colore del testo leggibile sullo sfondo dato (chiaro su scuro, scuro su chiaro).</summary>
    private static Brush ReadableOn(Brush bg)
    {
        if (bg is SolidColorBrush s && s.Color.A >= 200)
        {
            var c = s.Color;
            var lum = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255;
            if (lum > 0.62) return Hex("#14151A");
        }
        return Fg;
    }

    public static Button Btn(string text, RoutedEventHandler onClick, Brush? bg = null, string? tooltip = null,
        Brush? border = null)
    {
        bg ??= Surface2;
        var b = new Button
        {
            Content = text,
            Style = FlatButton,
            Background = bg,
            Foreground = ReadableOn(bg),
            BorderBrush = border ?? Border,
            Padding = new Thickness(10, 6, 10, 6),
            Margin = new Thickness(2),
            Cursor = Cursors.Hand,
            FontSize = 12,
            FontFamily = Font,
            ToolTip = tooltip,
        };
        b.Click += onClick;
        return b;
    }

    /// <summary>Pulsante del tipo di attività: sfondo scuro con una sfumatura e un bordo del colore della categoria.</summary>
    public static Button CategoryBtn(string name, string colorHex, RoutedEventHandler onClick) =>
        Btn(name, onClick, Hex(Mix(colorHex, Theme.Surface2, 0.22)), null, Hex(Mix(colorHex, Theme.Surface2, 0.75)));

    /// <summary>Mescola due colori: <paramref name="share"/> è la quota di <paramref name="a"/>.</summary>
    private static string Mix(string a, string b, double share)
    {
        var ca = (Color)ColorConverter.ConvertFromString(a);
        var cb = (Color)ColorConverter.ConvertFromString(b);
        byte M(byte x, byte y) => (byte)Math.Round(x * share + y * (1 - share));
        return $"#{M(ca.R, cb.R):X2}{M(ca.G, cb.G):X2}{M(ca.B, cb.B):X2}";
    }

    public static TextBlock Text(string text, double size = 12, bool bold = false, Brush? fg = null) => new()
    {
        Text = text,
        FontSize = size,
        FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
        Foreground = fg ?? Fg,
        TextWrapping = TextWrapping.Wrap,
    };

    public static TextBox Input() => new() { FontSize = 12, FontFamily = Font };

    /// <summary>Etichetta piccola sopra il controllo.</summary>
    public static StackPanel Field(string label, UIElement control)
    {
        var p = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        var l = Text(label, 10.5, fg: Subtle);
        l.Margin = new Thickness(2, 0, 0, 3);
        p.Children.Add(l);
        p.Children.Add(control);
        return p;
    }

    public static string Hms(TimeSpan t) => $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}";
}
