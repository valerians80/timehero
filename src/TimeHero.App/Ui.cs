using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;

namespace TimeHero.App;

/// <summary>Colori "sticky note" e piccoli costruttori di controlli (la UI è scritta in codice).</summary>
internal static class Ui
{
    public static SolidColorBrush Hex(string hex)
    {
        var b = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        b.Freeze();
        return b;
    }

    public static readonly Brush NoteBg = Hex("#FFF3A8");
    public static readonly Brush NoteEdge = Hex("#E3CE5C");
    public static readonly Brush Panel = Hex("#FFFBD9");
    public static readonly Brush Ink = Hex("#2B2A23");
    public static readonly Brush Subtle = Hex("#6B6840");
    public static readonly Brush Danger = Hex("#F4A6A0");
    public static readonly Brush Go = Hex("#B7E4B4");

    private static readonly Style FlatButton = (Style)XamlReader.Parse("""
        <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
               xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="Button">
          <Setter Property="Template">
            <Setter.Value>
              <ControlTemplate TargetType="Button">
                <Border x:Name="b" Background="{TemplateBinding Background}" CornerRadius="6"
                        Padding="{TemplateBinding Padding}" BorderBrush="#33000000" BorderThickness="1">
                  <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
                </Border>
                <ControlTemplate.Triggers>
                  <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="b" Property="Opacity" Value="0.8"/></Trigger>
                  <Trigger Property="IsPressed" Value="True"><Setter TargetName="b" Property="Opacity" Value="0.6"/></Trigger>
                  <Trigger Property="IsEnabled" Value="False"><Setter TargetName="b" Property="Opacity" Value="0.4"/></Trigger>
                </ControlTemplate.Triggers>
              </ControlTemplate>
            </Setter.Value>
          </Setter>
        </Style>
        """);

    public static Button Btn(string text, RoutedEventHandler onClick, Brush? bg = null, string? tooltip = null)
    {
        var b = new Button
        {
            Content = text,
            Style = FlatButton,
            Background = bg ?? Panel,
            Foreground = Ink,
            Padding = new Thickness(10, 6, 10, 6),
            Margin = new Thickness(2),
            Cursor = Cursors.Hand,
            FontSize = 12,
            ToolTip = tooltip,
        };
        b.Click += onClick;
        return b;
    }

    public static TextBlock Text(string text, double size = 12, bool bold = false, Brush? fg = null) => new()
    {
        Text = text,
        FontSize = size,
        FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
        Foreground = fg ?? Ink,
        TextWrapping = TextWrapping.Wrap,
    };

    public static TextBox Input() => new()
    {
        Padding = new Thickness(4, 3, 4, 3),
        Background = Panel,
        BorderBrush = NoteEdge,
        FontSize = 12,
    };

    /// <summary>Etichetta piccola sopra il controllo.</summary>
    public static StackPanel Field(string label, UIElement control)
    {
        var p = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
        p.Children.Add(Text(label, 10.5, fg: Subtle));
        p.Children.Add(control);
        return p;
    }

    public static string Hms(TimeSpan t) => $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}";
}
