using System.Globalization;
using System.Windows;

namespace BveTsStructureEditor.App.Views;

/// <summary>正の数を 1 つ入れてもらう小さなダイアログ。</summary>
public partial class InputDialog : Window
{
    public float Result { get; private set; }

    public InputDialog(string title, string prompt, string unit, float initial)
    {
        InitializeComponent();
        Title = title;
        Prompt.Text = prompt;
        Unit.Text = unit;
        Value.Text = initial.ToString("0.###", CultureInfo.InvariantCulture);
        Loaded += (_, _) => { Value.Focus(); Value.SelectAll(); };
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (float.TryParse(Value.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v > 0)
        {
            Result = v;
            DialogResult = true;
            return;
        }
        Error.Text = "正の数を入れてください";
        Error.Visibility = Visibility.Visible;
    }
}
