using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using Microsoft.Win32;

// Zayit's look (desktop 0.6.4, the Islands theme): islands on a grey canvas, its accent (Gold unless changed in
// Zayit), Noto Serif Hebrew, cards with big round corners and a soft border, the title bar tinted with the accent.
// Light or dark and the accent are Zayit's own settings, so the tool looks as Zayit does on this computer.
static class Theme
{
    // Segoe Fluent Icons (Windows 11), Segoe MDL2 Assets (Windows 10): the same code points in both.
    public const string G_MIN = "", G_MAX = "", G_RESTORE = "", G_CLOSE = "",
                        G_FOLDER = "", G_DRIVE = "", G_USB = "", G_CHECK = "", G_WARN = "",
                        G_BACK = "", G_COPY = "", G_DELETE = "", G_INFO = "", G_OK = "",
                        G_HISTORY = "", G_SWITCH = "", G_OPEN = "";

    public static readonly bool Dark;
    public static readonly Dictionary<string, string> P;
    public static readonly FontFamily Font, Icons;

    static Theme()
    {
        var mode = Prefs.Get("theme_mode", "System");
        if (mode == "Dark") Dark = true;
        else if (mode == "Light" || mode == "LightWithLightHeader") Dark = false;
        else
        {
            try
            {
                var v = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1);
                Dark = v is int && (int)v == 0;
            }
            catch { }
        }
        var forced = Environment.GetEnvironmentVariable("ZAYITMOVE_THEME");
        if (forced == "dark" || forced == "light") Dark = forced == "dark";

        // Jewel's Islands palette as Zayit draws it (sampled from Zayit's own window)
        P = Dark
            ? new Dictionary<string, string> {
                { "CANVAS", "#1E1F22" }, { "PANEL", "#2B2D30" }, { "TEXT", "#DFE1E5" }, { "SUB", "#8C9098" },
                { "BORDER", "#393B40" }, { "OUTLINE", "#4E5157" }, { "HOVER", "#393B40" }, { "FIELD", "#2B2D30" },
                { "RED", "#F28B82" }, { "REDSOFT", "#45282A" }, { "WARN", "#E0B45B" }, { "WARNSOFT", "#3F3420" },
                { "TIP", "#3C3F44" }, { "TIPTEXT", "#DFE1E5" } }
            : new Dictionary<string, string> {
                { "CANVAS", "#E8E9EB" }, { "PANEL", "#F7F8FA" }, { "TEXT", "#000000" }, { "SUB", "#818594" },
                { "BORDER", "#EBECF0" }, { "OUTLINE", "#C9CCD6" }, { "HOVER", "#EBECF0" }, { "FIELD", "#FFFFFF" },
                { "RED", "#C7222D" }, { "REDSOFT", "#FCE9EA" }, { "WARN", "#8A6100" }, { "WARNSOFT", "#FBF1D9" },
                { "TIP", "#2B2D30" }, { "TIPTEXT", "#DFE1E5" } };
        var accent = Accent(Prefs.Get("accent_color", "Gold"), Dark);
        P["ACCENT"] = accent;
        P["ONACCENT"] = "#FFFFFF";
        P["ACCENTSOFT"] = Mix(accent, P["PANEL"], 0.14);
        P["ACCENTHOVER"] = Mix(accent, Dark ? "#FFFFFF" : "#000000", 0.10);
        // The title bar's tint: the accent at 25% over the panel (Zayit's titleBarGradientColor)
        P["TITLE"] = Mix(accent, P["PANEL"], 0.25);

        Font = LoadFont();
        Icons = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
    }

    // Zayit's AccentColor presets.
    static string Accent(string name, bool dark)
    {
        switch (name)
        {
            case "Teal": return dark ? "#2FC2B6" : "#1A998E";
            case "Green": return dark ? "#5AB869" : "#3D9A50";
            case "Default":
            case "System": return dark ? "#3574F0" : "#4682FA";
            default: return dark ? "#D4A843" : "#BE9117";
        }
    }

    static string Mix(string a, string b, double t)
    {
        var x = (Color)ColorConverter.ConvertFromString(a);
        var y = (Color)ColorConverter.ConvertFromString(b);
        Func<byte, byte, byte> m = (p, q) => (byte)Math.Round(q * (1 - t) + p * t);
        return string.Format("#{0:X2}{1:X2}{2:X2}", m(x.R, y.R), m(x.G, y.G), m(x.B, y.B));
    }

    // Noto Serif Hebrew, Zayit's text font: inside the exe, unpacked once next to the tool's settings.
    static FontFamily LoadFont()
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Zayit Move", "fonts");
            var file = Path.Combine(dir, "notoserifhebrew.ttf");
            var data = Resource("notoserifhebrew.ttf");
            if (data != null && (!File.Exists(file) || new FileInfo(file).Length != data.Length))
            {
                Directory.CreateDirectory(dir);
                File.WriteAllBytes(file, data);
            }
            if (File.Exists(file)) return new FontFamily(new Uri(dir + "\\"), "./#Noto Serif Hebrew, Segoe UI");
        }
        catch { }
        return new FontFamily("Segoe UI");
    }

    public static byte[] Resource(string name)
    {
        using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
        {
            if (s == null) return null;
            var m = new MemoryStream();
            s.CopyTo(m);
            return m.ToArray();
        }
    }

    public static BitmapSource Image(string name)
    {
        var data = Resource(name);
        if (data == null) return null;
        var img = new BitmapImage();
        img.BeginInit();
        img.StreamSource = new MemoryStream(data);
        img.CacheOption = BitmapCacheOption.OnLoad;
        img.EndInit();
        img.Freeze();
        return img;
    }

    static readonly Dictionary<string, SolidColorBrush> brushes = new Dictionary<string, SolidColorBrush>();

    public static SolidColorBrush B(string key)
    {
        lock (brushes)
        {
            SolidColorBrush b;
            if (!brushes.TryGetValue(key, out b))
            {
                b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(P.ContainsKey(key) ? P[key] : key));
                b.Freeze();
                brushes[key] = b;
            }
            return b;
        }
    }

    public static SolidColorBrush B(string key, double alpha)
    {
        var c = B(key).Color;
        var b = new SolidColorBrush(Color.FromArgb((byte)Math.Round(alpha * 255), c.R, c.G, c.B));
        b.Freeze();
        return b;
    }

    const string Styles = @"
<ResourceDictionary xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>
  <!-- Jewel's buttons with the Islands corners (8): the default one filled with the accent, the outlined one quiet. -->
  <Style x:Key='Outlined' TargetType='Button'>
    <Setter Property='Background' Value='$FIELD$'/>
    <Setter Property='Foreground' Value='$TEXT$'/>
    <Setter Property='BorderBrush' Value='$OUTLINE$'/>
    <Setter Property='BorderThickness' Value='1'/>
    <Setter Property='Padding' Value='14,5,14,6'/>
    <Setter Property='FontSize' Value='13'/>
    <Setter Property='Cursor' Value='Hand'/>
    <Setter Property='FocusVisualStyle' Value='{x:Null}'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='Button'>
          <Border x:Name='b' Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}'
                  BorderThickness='{TemplateBinding BorderThickness}' CornerRadius='8' Padding='{TemplateBinding Padding}'>
            <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='b' Property='Background' Value='$HOVER$'/></Trigger>
            <Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='b' Property='BorderBrush' Value='$ACCENT$'/></Trigger>
            <Trigger Property='IsEnabled' Value='False'><Setter TargetName='b' Property='Opacity' Value='0.45'/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
  <Style x:Key='Default' TargetType='Button' BasedOn='{StaticResource Outlined}'>
    <Setter Property='Background' Value='$ACCENT$'/>
    <Setter Property='BorderBrush' Value='$ACCENT$'/>
    <Setter Property='Foreground' Value='$ONACCENT$'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='Button'>
          <Border x:Name='b' Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}'
                  BorderThickness='1' CornerRadius='8' Padding='{TemplateBinding Padding}'>
            <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='b' Property='Background' Value='$ACCENTHOVER$'/></Trigger>
            <Trigger Property='IsPressed' Value='True'><Setter TargetName='b' Property='Opacity' Value='0.85'/></Trigger>
            <Trigger Property='IsEnabled' Value='False'><Setter TargetName='b' Property='Opacity' Value='0.45'/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
  <Style x:Key='Danger' TargetType='Button' BasedOn='{StaticResource Default}'>
    <Setter Property='Background' Value='$RED$'/>
    <Setter Property='BorderBrush' Value='$RED$'/>
  </Style>
  <!-- A link: the accent, no frame (Zayit's links are in the accent too). -->
  <Style x:Key='Link' TargetType='Button'>
    <Setter Property='Foreground' Value='$ACCENT$'/>
    <Setter Property='FontSize' Value='13'/>
    <Setter Property='Cursor' Value='Hand'/>
    <Setter Property='FocusVisualStyle' Value='{x:Null}'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='Button'>
          <ContentPresenter x:Name='c'/>
          <ControlTemplate.Triggers>
            <Trigger Property='IsMouseOver' Value='True'><Setter Property='Opacity' Value='0.8'/></Trigger>
            <Trigger Property='IsEnabled' Value='False'><Setter Property='Opacity' Value='0.45'/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
  <!-- The title bar's window buttons; close turns red under the mouse, as in Zayit. -->
  <Style x:Key='WinBtn' TargetType='Button'>
    <Setter Property='Foreground' Value='$TEXT$'/>
    <Setter Property='Background' Value='Transparent'/>
    <Setter Property='Width' Value='46'/>
    <Setter Property='Height' Value='40'/>
    <Setter Property='FocusVisualStyle' Value='{x:Null}'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='Button'>
          <Border x:Name='b' Background='{TemplateBinding Background}'>
            <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='b' Property='Background' Value='$HOVER$'/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
  <Style x:Key='WinClose' TargetType='Button' BasedOn='{StaticResource WinBtn}'>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='Button'>
          <Border x:Name='b' Background='{TemplateBinding Background}'>
            <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property='IsMouseOver' Value='True'>
              <Setter TargetName='b' Property='Background' Value='#C42B1C'/>
              <Setter Property='Foreground' Value='White'/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
  <!-- Jewel's check box: a rounded square (Islands corner 5) filled with the accent when on. -->
  <Style TargetType='CheckBox'>
    <Setter Property='Foreground' Value='$TEXT$'/>
    <Setter Property='FontSize' Value='14'/>
    <Setter Property='Cursor' Value='Hand'/>
    <Setter Property='FocusVisualStyle' Value='{x:Null}'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='CheckBox'>
          <Grid Background='Transparent'>
            <Grid.ColumnDefinitions><ColumnDefinition Width='Auto'/><ColumnDefinition/></Grid.ColumnDefinitions>
            <Border x:Name='box' Width='17' Height='17' CornerRadius='5' BorderThickness='1' BorderBrush='$OUTLINE$' Background='$FIELD$' VerticalAlignment='Center'>
              <TextBlock x:Name='tick' Text='&#xE73E;' FontFamily='Segoe Fluent Icons, Segoe MDL2 Assets' FontSize='11' Foreground='$ONACCENT$'
                         HorizontalAlignment='Center' VerticalAlignment='Center' Visibility='Collapsed'/>
            </Border>
            <ContentPresenter Grid.Column='1' Margin='9,0,0,0' VerticalAlignment='Center'/>
          </Grid>
          <ControlTemplate.Triggers>
            <Trigger Property='IsChecked' Value='True'>
              <Setter TargetName='box' Property='Background' Value='$ACCENT$'/>
              <Setter TargetName='box' Property='BorderBrush' Value='$ACCENT$'/>
              <Setter TargetName='tick' Property='Visibility' Value='Visible'/>
            </Trigger>
            <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='box' Property='BorderBrush' Value='$ACCENT$'/></Trigger>
            <Trigger Property='IsEnabled' Value='False'><Setter Property='Opacity' Value='0.45'/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
  <Style TargetType='ToolTip'>
    <Setter Property='OverridesDefaultStyle' Value='True'/>
    <Setter Property='HasDropShadow' Value='False'/>
    <Setter Property='Foreground' Value='$TIPTEXT$'/>
    <Setter Property='FontSize' Value='12'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='ToolTip'>
          <Border Background='$TIP$' CornerRadius='8' Padding='10,6,10,7' MaxWidth='340'><ContentPresenter/></Border>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
  <!-- A thin scroll bar, as Zayit's -->
  <Style TargetType='ScrollBar'>
    <Setter Property='Width' Value='8'/>
    <Setter Property='MinWidth' Value='8'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='ScrollBar'>
          <Track x:Name='PART_Track' IsDirectionReversed='True'>
            <Track.Thumb>
              <Thumb>
                <Thumb.Template>
                  <ControlTemplate TargetType='Thumb'><Border CornerRadius='3' Background='$OUTLINE$' Margin='1,2'/></ControlTemplate>
                </Thumb.Template>
              </Thumb>
            </Track.Thumb>
          </Track>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
</ResourceDictionary>";

    public static ResourceDictionary Resources()
    {
        var x = Styles;
        foreach (var kv in P) x = x.Replace("$" + kv.Key + "$", kv.Value);
        return (ResourceDictionary)XamlReader.Parse(x);
    }

    // The window: our own title bar (WindowChrome), right to left, Zayit's font and colours.
    public static void Dress(Window w)
    {
        w.Resources.MergedDictionaries.Add(Resources());
        w.FlowDirection = FlowDirection.RightToLeft;
        w.FontFamily = Font;
        w.FontSize = 14;
        w.Foreground = B("TEXT");
        w.Background = B("CANVAS");
        w.UseLayoutRounding = true;
        w.SnapsToDevicePixels = true;
        TextOptions.SetTextFormattingMode(w, TextFormattingMode.Display);
        TextOptions.SetTextRenderingMode(w, TextRenderingMode.ClearType);
        WindowChrome.SetWindowChrome(w, new WindowChrome { CaptionHeight = 40, ResizeBorderThickness = new Thickness(6), GlassFrameThickness = new Thickness(-1), UseAeroCaptionButtons = false, CornerRadius = new CornerRadius(0) });
        w.SourceInitialized += (s, e) =>
        {
            int dark = Dark ? 1 : 0;
            DwmSetWindowAttribute(new WindowInteropHelper(w).Handle, 20, ref dark, 4);
        };
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int val, int size);

    // Zayit's title bar: the accent's tint fading into the panel colour, the title in the middle, the window buttons
    // at the window's far side.
    public static Grid TitleBar(Window w, string title, BitmapSource icon)
    {
        var g = new Grid { Height = 40 };
        g.Background = new LinearGradientBrush(
            new GradientStopCollection { new GradientStop(B("TITLE").Color, 0), new GradientStop(B("PANEL").Color, 0.6) }, new Point(1, 0), new Point(0, 0));
        var brand = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        if (icon != null) brand.Children.Add(Picture(icon, 18, 18));
        brand.Children.Add(new TextBlock { Text = title, FontSize = 13, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        g.Children.Add(brand);
        // Right to left like the window: minimize nearest the title, close at the window's left edge, as in Zayit
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(WinBtn(w, G_MIN, "מזעור", "WinBtn", () => SystemCommands.MinimizeWindow(w)));
        var max = WinBtn(w, G_MAX, "הגדלה", "WinBtn", () =>
        {
            if (w.WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(w); else SystemCommands.MaximizeWindow(w);
        });
        w.StateChanged += (s, e) => ((TextBlock)max.Content).Text = w.WindowState == WindowState.Maximized ? G_RESTORE : G_MAX;
        buttons.Children.Add(max);
        buttons.Children.Add(WinBtn(w, G_CLOSE, "סגירה", "WinClose", () => SystemCommands.CloseWindow(w)));
        // In a right-to-left window "Right" is the left edge: where Zayit has them
        g.Children.Add(buttons);
        return g;
    }

    static Button WinBtn(Window w, string glyph, string name, string style, Action click)
    {
        var b = new Button { Content = new TextBlock { Text = glyph, FontFamily = Icons, FontSize = 10 }, Style = (Style)w.FindResource(style), ToolTip = name };
        WindowChrome.SetIsHitTestVisibleInChrome(b, true);
        b.Click += (s, e) => click();
        return b;
    }

    // ---------- building blocks ----------

    // A picture as it is: WPF would mirror it inside a right-to-left window.
    public static System.Windows.Controls.Image Picture(BitmapSource src, double w, double h)
    {
        var img = new System.Windows.Controls.Image { Source = src, Width = w, Height = h, FlowDirection = FlowDirection.LeftToRight };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
        return img;
    }

    public static TextBlock T(string text, double size = 14, string color = "TEXT", bool bold = false)
    {
        return new TextBlock { Text = text, FontSize = size, Foreground = B(color), FontWeight = bold ? FontWeights.Bold : FontWeights.Normal, TextWrapping = TextWrapping.Wrap };
    }

    // A path inside Hebrew text: left to right, aligned to the right, cut at its start when long.
    // A path inside Hebrew text: left to right, aligned to the right, shortened in its middle so its end shows.
    public static TextBlock PathText(string path, double size = 12.5, string color = "SUB", int max = 72)
    {
        return new TextBlock
        {
            Text = Fmt.ShortPath(path, max), FontSize = size, Foreground = B(color), FlowDirection = FlowDirection.LeftToRight,
            TextAlignment = TextAlignment.Right, HorizontalAlignment = HorizontalAlignment.Stretch, TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = path
        };
    }

    // "מ־" or "אל" and a path after it.
    public static Grid PathRow(string label, string path)
    {
        var g = new Grid { Margin = new Thickness(0, 1, 0, 1) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.Children.Add(T(label, 12.5, "SUB"));
        var p = PathText(path, 12.5, "SUB", 64);
        p.TextAlignment = TextAlignment.Left;
        p.FlowDirection = FlowDirection.LeftToRight;
        p.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(p, 1);
        g.Children.Add(p);
        return g;
    }

    public static TextBlock Glyph(string g, double size, string color)
    {
        return new TextBlock { Text = g, FontFamily = Icons, FontSize = size, Foreground = B(color), VerticalAlignment = VerticalAlignment.Center };
    }

    // Zayit's home widgets: corners of 18, a soft border, the panel's colour, a little see-through over the wallpaper.
    public static Border Card(params UIElement[] rows)
    {
        var sp = new StackPanel();
        foreach (var r in rows) if (r != null) sp.Children.Add(r);
        return new Border
        {
            CornerRadius = new CornerRadius(18), BorderThickness = new Thickness(1.5), BorderBrush = B(Dark ? "BORDER" : "BORDER"),
            Background = B("PANEL", Dark ? 0.78 : 0.86), Padding = new Thickness(18, 14, 18, 16), Margin = new Thickness(0, 0, 0, 12), Child = sp
        };
    }

    public static TextBlock Title(string text)
    {
        var t = T(text, 15.5, "TEXT", true);
        t.Margin = new Thickness(0, 0, 0, 8);
        return t;
    }

    // A name and its value at the far side, as the times in Zayit's zmanim widget.
    public static Grid Pair(string label, string value)
    {
        var g = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.Children.Add(T(label, 13, "SUB"));
        var v = T(value, 13);
        Grid.SetColumn(v, 1);
        g.Children.Add(v);
        return g;
    }

    public static Button Btn(Window w, string text, string style, Action click)
    {
        var b = new Button { Content = text, Style = (Style)w.FindResource(style) };
        if (click != null) b.Click += (s, e) => click();
        return b;
    }

    public static Button IconBtn(Window w, string glyph, string text, string style, Action click)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(new TextBlock { Text = glyph, FontFamily = Icons, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 7, 0) });
        sp.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        var b = new Button { Content = sp, Style = (Style)w.FindResource(style) };
        System.Windows.Automation.AutomationProperties.SetName(b, text);
        if (click != null) b.Click += (s, e) => click();
        return b;
    }

    // A message with an icon on a tinted background: a warning, an error, or done.
    public static Border Note(string glyph, string text, string fg, string bg)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        var icon = Glyph(glyph, 14, fg);
        icon.VerticalAlignment = VerticalAlignment.Top;
        icon.Margin = new Thickness(0, 3, 9, 0);
        g.Children.Add(icon);
        var t = T(text, 13, fg);
        Grid.SetColumn(t, 1);
        g.Children.Add(t);
        return new Border { Background = B(bg), CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 8, 12, 9), Margin = new Thickness(0, 4, 0, 4), Child = g };
    }

    public static StackPanel Row(params UIElement[] items)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        foreach (var i in items)
        {
            if (i == null) continue;
            var f = i as FrameworkElement;
            if (f != null && sp.Children.Count > 0) f.Margin = new Thickness(8, 0, 0, 0);
            sp.Children.Add(i);
        }
        return sp;
    }
}
