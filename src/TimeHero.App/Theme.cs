using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;

namespace TimeHero.App;

/// <summary>Tema scuro: palette e stili dei controlli standard di WPF (che di base sono chiari).</summary>
internal static class Theme
{
    public const string Bg = "#1B1C21";
    public const string Surface = "#25272E";
    public const string Surface2 = "#2F323B";
    public const string RowAlt = "#2A2C34";
    public const string Border = "#3A3D47";
    public const string Text = "#ECEDEF";
    public const string Subtle = "#9AA0AB";
    public const string Accent = "#6EA8FE";
    public const string AccentDim = "#2C4A78";
    public const string Danger = "#C94F4A";
    public const string Ok = "#2F9E6E";

    private const string Ns = "xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" " +
                              "xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"";

    /// <summary>Registra gli stili impliciti. Un errore in uno stile non blocca l'app: quel controllo resta chiaro.</summary>
    public static void Apply(Application app)
    {
        // i colori di sistema usati dai template standard (liste, griglie, angoli degli scroll…)
        void Sys(object key, string hex) => app.Resources[key] = Ui.Hex(hex);
        Sys(SystemColors.WindowBrushKey, Surface);
        Sys(SystemColors.WindowTextBrushKey, Text);
        Sys(SystemColors.ControlBrushKey, Surface);
        Sys(SystemColors.ControlTextBrushKey, Text);
        Sys(SystemColors.ControlLightBrushKey, Surface2);
        Sys(SystemColors.ControlLightLightBrushKey, Border);
        Sys(SystemColors.ControlDarkBrushKey, Border);
        Sys(SystemColors.HighlightBrushKey, AccentDim);
        Sys(SystemColors.HighlightTextBrushKey, Text);
        Sys(SystemColors.InactiveSelectionHighlightBrushKey, AccentDim);
        Sys(SystemColors.InactiveSelectionHighlightTextBrushKey, Text);
        Sys(SystemColors.GrayTextBrushKey, Subtle);

        foreach (var xaml in Styles)
        {
            try
            {
                var style = (Style)XamlReader.Parse(Tokenize(xaml));
                app.Resources[style.TargetType] = style;
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"TimeHero: stile non applicato: {ex.Message}");
            }
        }
    }

    public static string Tokenize(string xaml) => xaml
        .Replace("@NS@", Ns)
        .Replace("@Bg@", Bg).Replace("@Surface2@", Surface2).Replace("@Surface@", Surface)
        .Replace("@Border@", Border).Replace("@Text@", Text).Replace("@Subtle@", Subtle)
        .Replace("@AccentDim@", AccentDim).Replace("@Accent@", Accent);

    /// <summary>Barra del titolo scura per le finestre normali (Windows 10 2004+ / 11).</summary>
    public static void DarkTitleBar(Window w)
    {
        try
        {
            var hwnd = new WindowInteropHelper(w).Handle;
            var on = 1;
            if (DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int)) != 0)
                DwmSetWindowAttribute(hwnd, 19, ref on, sizeof(int));
        }
        catch
        {
            // titolo chiaro: non è un problema
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private static readonly string[] Styles =
    {
        // ---------- TextBox ----------
        """
        <Style @NS@ TargetType="TextBox">
          <Setter Property="Background" Value="@Surface@"/>
          <Setter Property="Foreground" Value="@Text@"/>
          <Setter Property="BorderBrush" Value="@Border@"/>
          <Setter Property="BorderThickness" Value="1"/>
          <Setter Property="CaretBrush" Value="@Text@"/>
          <Setter Property="SelectionBrush" Value="@Accent@"/>
          <Setter Property="Padding" Value="4,4"/>
          <Setter Property="Template">
            <Setter.Value>
              <ControlTemplate TargetType="TextBox">
                <Border x:Name="bd" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}"
                        BorderThickness="{TemplateBinding BorderThickness}" CornerRadius="6">
                  <ScrollViewer x:Name="PART_ContentHost" Padding="{TemplateBinding Padding}"/>
                </Border>
                <ControlTemplate.Triggers>
                  <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="bd" Property="BorderBrush" Value="@Accent@"/></Trigger>
                  <Trigger Property="IsEnabled" Value="False"><Setter TargetName="bd" Property="Opacity" Value="0.5"/></Trigger>
                </ControlTemplate.Triggers>
              </ControlTemplate>
            </Setter.Value>
          </Setter>
        </Style>
        """,

        // ---------- ComboBox ----------
        """
        <Style @NS@ TargetType="ComboBox">
          <Setter Property="Foreground" Value="@Text@"/>
          <Setter Property="MinHeight" Value="28"/>
          <Setter Property="Template">
            <Setter.Value>
              <ControlTemplate TargetType="ComboBox">
                <Grid>
                  <ToggleButton x:Name="tb" Focusable="False" ClickMode="Press"
                                IsChecked="{Binding IsDropDownOpen, Mode=TwoWay, RelativeSource={RelativeSource TemplatedParent}}">
                    <ToggleButton.Template>
                      <ControlTemplate TargetType="ToggleButton">
                        <Border x:Name="b" Background="@Surface@" BorderBrush="@Border@" BorderThickness="1" CornerRadius="6">
                          <Path HorizontalAlignment="Right" VerticalAlignment="Center" Margin="0,0,10,0"
                                Data="M 0 0 L 4 4 L 8 0" Stroke="@Subtle@" StrokeThickness="1.6"/>
                        </Border>
                        <ControlTemplate.Triggers>
                          <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="b" Property="BorderBrush" Value="@Accent@"/></Trigger>
                        </ControlTemplate.Triggers>
                      </ControlTemplate>
                    </ToggleButton.Template>
                  </ToggleButton>
                  <ContentPresenter x:Name="cs" IsHitTestVisible="False" Margin="8,4,28,4"
                                    VerticalAlignment="Center" HorizontalAlignment="Left"
                                    Content="{TemplateBinding SelectionBoxItem}"
                                    ContentTemplate="{TemplateBinding SelectionBoxItemTemplate}"
                                    ContentTemplateSelector="{TemplateBinding ItemTemplateSelector}"/>
                  <TextBox x:Name="PART_EditableTextBox" Style="{x:Null}" Visibility="Collapsed" Margin="6,2,26,2"
                           Background="Transparent" Foreground="@Text@" CaretBrush="@Text@" SelectionBrush="@Accent@"
                           BorderThickness="0" VerticalAlignment="Center" Focusable="True"
                           IsReadOnly="{TemplateBinding IsReadOnly}"/>
                  <Popup x:Name="PART_Popup" Placement="Bottom" IsOpen="{TemplateBinding IsDropDownOpen}"
                         AllowsTransparency="True" Focusable="False" PopupAnimation="Fade">
                    <Border Margin="0,4,0,0" MinWidth="{Binding ActualWidth, RelativeSource={RelativeSource TemplatedParent}}"
                            MaxHeight="{TemplateBinding MaxDropDownHeight}" Background="@Surface2@" BorderBrush="@Border@"
                            BorderThickness="1" CornerRadius="6" Padding="2">
                      <ScrollViewer><ItemsPresenter KeyboardNavigation.DirectionalNavigation="Contained"/></ScrollViewer>
                    </Border>
                  </Popup>
                </Grid>
                <ControlTemplate.Triggers>
                  <Trigger Property="IsEditable" Value="True">
                    <Setter TargetName="cs" Property="Visibility" Value="Collapsed"/>
                    <Setter TargetName="PART_EditableTextBox" Property="Visibility" Value="Visible"/>
                  </Trigger>
                  <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.5"/></Trigger>
                </ControlTemplate.Triggers>
              </ControlTemplate>
            </Setter.Value>
          </Setter>
        </Style>
        """,

        """
        <Style @NS@ TargetType="ComboBoxItem">
          <Setter Property="Foreground" Value="@Text@"/>
          <Setter Property="Padding" Value="8,5"/>
          <Setter Property="Template">
            <Setter.Value>
              <ControlTemplate TargetType="ComboBoxItem">
                <Border x:Name="b" Background="Transparent" CornerRadius="4" Padding="{TemplateBinding Padding}">
                  <ContentPresenter/>
                </Border>
                <ControlTemplate.Triggers>
                  <Trigger Property="IsHighlighted" Value="True"><Setter TargetName="b" Property="Background" Value="@Border@"/></Trigger>
                  <Trigger Property="IsSelected" Value="True"><Setter TargetName="b" Property="Background" Value="@AccentDim@"/></Trigger>
                </ControlTemplate.Triggers>
              </ControlTemplate>
            </Setter.Value>
          </Setter>
        </Style>
        """,

        // ---------- ListBox ----------
        """
        <Style @NS@ TargetType="ListBox">
          <Setter Property="Background" Value="@Surface@"/>
          <Setter Property="Foreground" Value="@Text@"/>
          <Setter Property="BorderBrush" Value="@Border@"/>
          <Setter Property="BorderThickness" Value="1"/>
          <Setter Property="Template">
            <Setter.Value>
              <ControlTemplate TargetType="ListBox">
                <Border Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}"
                        BorderThickness="{TemplateBinding BorderThickness}" CornerRadius="6" Padding="2">
                  <ScrollViewer Focusable="False"><ItemsPresenter/></ScrollViewer>
                </Border>
              </ControlTemplate>
            </Setter.Value>
          </Setter>
        </Style>
        """,

        """
        <Style @NS@ TargetType="ListBoxItem">
          <Setter Property="Foreground" Value="@Text@"/>
          <Setter Property="Padding" Value="6,4"/>
          <Setter Property="Template">
            <Setter.Value>
              <ControlTemplate TargetType="ListBoxItem">
                <Border x:Name="b" Background="Transparent" CornerRadius="4" Padding="{TemplateBinding Padding}">
                  <ContentPresenter/>
                </Border>
                <ControlTemplate.Triggers>
                  <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="b" Property="Background" Value="@Surface2@"/></Trigger>
                  <Trigger Property="IsSelected" Value="True"><Setter TargetName="b" Property="Background" Value="@AccentDim@"/></Trigger>
                </ControlTemplate.Triggers>
              </ControlTemplate>
            </Setter.Value>
          </Setter>
        </Style>
        """,

        // ---------- CheckBox ----------
        """
        <Style @NS@ TargetType="CheckBox">
          <Setter Property="Foreground" Value="@Text@"/>
          <Setter Property="Template">
            <Setter.Value>
              <ControlTemplate TargetType="CheckBox">
                <StackPanel Orientation="Horizontal" Background="Transparent">
                  <Border x:Name="box" Width="18" Height="18" CornerRadius="5" Background="@Surface@" BorderBrush="@Border@"
                          BorderThickness="1" VerticalAlignment="Center">
                    <Path x:Name="tick" Visibility="Collapsed" Data="M 4 9 L 7.5 12.5 L 13.5 5" Stroke="White" StrokeThickness="2"/>
                  </Border>
                  <ContentPresenter Margin="8,0,0,0" VerticalAlignment="Center"/>
                </StackPanel>
                <ControlTemplate.Triggers>
                  <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="box" Property="BorderBrush" Value="@Accent@"/></Trigger>
                  <Trigger Property="IsChecked" Value="True">
                    <Setter TargetName="box" Property="Background" Value="@Accent@"/>
                    <Setter TargetName="box" Property="BorderBrush" Value="@Accent@"/>
                    <Setter TargetName="tick" Property="Visibility" Value="Visible"/>
                  </Trigger>
                  <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.5"/></Trigger>
                </ControlTemplate.Triggers>
              </ControlTemplate>
            </Setter.Value>
          </Setter>
        </Style>
        """,

        // ---------- TabControl ----------
        """
        <Style @NS@ TargetType="TabControl">
          <Setter Property="Template">
            <Setter.Value>
              <ControlTemplate TargetType="TabControl">
                <Grid>
                  <Grid.RowDefinitions><RowDefinition Height="Auto"/><RowDefinition Height="*"/></Grid.RowDefinitions>
                  <TabPanel Grid.Row="0" IsItemsHost="True" Margin="0,0,0,8" Background="Transparent"/>
                  <Border Grid.Row="1" Background="@Surface@" BorderBrush="@Border@" BorderThickness="1" CornerRadius="8">
                    <ContentPresenter ContentSource="SelectedContent"/>
                  </Border>
                </Grid>
              </ControlTemplate>
            </Setter.Value>
          </Setter>
        </Style>
        """,

        """
        <Style @NS@ TargetType="TabItem">
          <Setter Property="Template">
            <Setter.Value>
              <ControlTemplate TargetType="TabItem">
                <Border x:Name="b" Padding="14,7" Margin="0,0,4,0" Background="Transparent" CornerRadius="6">
                  <ContentPresenter x:Name="cp" ContentSource="Header" TextElement.Foreground="@Subtle@"/>
                </Border>
                <ControlTemplate.Triggers>
                  <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="b" Property="Background" Value="@Surface@"/></Trigger>
                  <Trigger Property="IsSelected" Value="True">
                    <Setter TargetName="b" Property="Background" Value="@AccentDim@"/>
                    <Setter TargetName="cp" Property="TextElement.Foreground" Value="@Text@"/>
                  </Trigger>
                </ControlTemplate.Triggers>
              </ControlTemplate>
            </Setter.Value>
          </Setter>
        </Style>
        """,

        // ---------- ScrollBar (sottile) ----------
        """
        <Style @NS@ TargetType="ScrollBar">
          <Setter Property="Background" Value="Transparent"/>
          <Setter Property="Width" Value="12"/>
          <Setter Property="Template">
            <Setter.Value>
              <ControlTemplate TargetType="ScrollBar">
                <Grid Background="Transparent">
                  <Track x:Name="PART_Track" IsDirectionReversed="True">
                    <Track.DecreaseRepeatButton><RepeatButton Command="ScrollBar.PageUpCommand" Opacity="0" Focusable="False"/></Track.DecreaseRepeatButton>
                    <Track.Thumb>
                      <Thumb><Thumb.Template><ControlTemplate TargetType="Thumb"><Border Background="@Border@" CornerRadius="4" Margin="3,2"/></ControlTemplate></Thumb.Template></Thumb>
                    </Track.Thumb>
                    <Track.IncreaseRepeatButton><RepeatButton Command="ScrollBar.PageDownCommand" Opacity="0" Focusable="False"/></Track.IncreaseRepeatButton>
                  </Track>
                </Grid>
              </ControlTemplate>
            </Setter.Value>
          </Setter>
          <Style.Triggers>
            <Trigger Property="Orientation" Value="Horizontal">
              <Setter Property="Width" Value="Auto"/>
              <Setter Property="Height" Value="12"/>
              <Setter Property="Template">
                <Setter.Value>
                  <ControlTemplate TargetType="ScrollBar">
                    <Grid Background="Transparent">
                      <Track x:Name="PART_Track">
                        <Track.DecreaseRepeatButton><RepeatButton Command="ScrollBar.PageLeftCommand" Opacity="0" Focusable="False"/></Track.DecreaseRepeatButton>
                        <Track.Thumb>
                          <Thumb><Thumb.Template><ControlTemplate TargetType="Thumb"><Border Background="@Border@" CornerRadius="4" Margin="2,3"/></ControlTemplate></Thumb.Template></Thumb>
                        </Track.Thumb>
                        <Track.IncreaseRepeatButton><RepeatButton Command="ScrollBar.PageRightCommand" Opacity="0" Focusable="False"/></Track.IncreaseRepeatButton>
                      </Track>
                    </Grid>
                  </ControlTemplate>
                </Setter.Value>
              </Setter>
            </Trigger>
          </Style.Triggers>
        </Style>
        """,
    };

    /// <summary>Colori del menu dell'icona nella tray (controllo WinForms).</summary>
    public sealed class DarkMenuColors : System.Windows.Forms.ProfessionalColorTable
    {
        private static System.Drawing.Color C(string hex)
        {
            var c = (Color)ColorConverter.ConvertFromString(hex);
            return System.Drawing.Color.FromArgb(c.R, c.G, c.B);
        }

        public override System.Drawing.Color ToolStripDropDownBackground => C(Surface);
        public override System.Drawing.Color ImageMarginGradientBegin => C(Surface);
        public override System.Drawing.Color ImageMarginGradientMiddle => C(Surface);
        public override System.Drawing.Color ImageMarginGradientEnd => C(Surface);
        public override System.Drawing.Color MenuBorder => C(Border);
        public override System.Drawing.Color MenuItemBorder => C(AccentDim);
        public override System.Drawing.Color MenuItemSelected => C(AccentDim);
        public override System.Drawing.Color MenuItemSelectedGradientBegin => C(AccentDim);
        public override System.Drawing.Color MenuItemSelectedGradientEnd => C(AccentDim);
        public override System.Drawing.Color SeparatorDark => C(Border);
        public override System.Drawing.Color SeparatorLight => C(Border);
    }
}
