using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace BveTsStructureEditor.App.Views;

/// <summary>
/// ツールバーのボタンに入れる「アイコン + 文字」。アイコンは Windows 標準のアイコンフォント
/// （Windows 11 は Segoe Fluent Icons、10 は Segoe MDL2 Assets。文字コードは共通）の 1 文字を使う。
/// <see cref="Text"/> が空ならアイコンだけになる。
/// </summary>
public class IconLabel : StackPanel
{
    private static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(
        nameof(Glyph), typeof(string), typeof(IconLabel),
        new PropertyMetadata("", (d, e) => ((IconLabel)d)._icon.Text = (string)e.NewValue));

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(IconLabel),
        new PropertyMetadata("", (d, e) => ((IconLabel)d).UpdateText()));

    private readonly TextBlock _icon = new()
    {
        FontFamily = IconFont,
        FontSize = 15,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private readonly TextBlock _text = new() { VerticalAlignment = VerticalAlignment.Center };

    public IconLabel()
    {
        Orientation = Orientation.Horizontal;
        Children.Add(_icon);
        Children.Add(_text);
        UpdateText();
    }

    public string Glyph
    {
        get => (string)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    private void UpdateText()
    {
        _text.Text = Text;
        bool has = !string.IsNullOrEmpty(Text);
        _text.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        _text.Margin = has ? new Thickness(5, 0, 0, 0) : new Thickness(0);
    }
}
