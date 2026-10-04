using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using BveTsStructureEditor.App.Document;
using BveTsStructureEditor.Core.Format;

namespace BveTsStructureEditor.App.Views;

/// <summary>「保存設定」タブ。値は開いているドキュメントの <see cref="XWriteOptions"/> に直接入れる。</summary>
public partial class SaveOptionsPanel : UserControl
{
    private IEditorHost? _host;
    private bool _updating;

    public SaveOptionsPanel()
    {
        InitializeComponent();
    }

    public void Attach(IEditorHost host) => _host = host;

    public void Refresh(ChangeKind kind)
    {
        if (_host == null || !kind.HasFlag(ChangeKind.File)) return;
        var o = _host.Document.WriteOptions;
        _updating = true;
        try
        {
            EncodingBox.SelectedIndex = o.Encoding.CodePage == 932 ? 0 : 1;
            PrecisionBox.Text = o.Precision.ToString(CultureInfo.InvariantCulture);
            TemplatesBox.IsChecked = o.WriteTemplates;
            PmxScaleBox.Text = _host.Document.PmxOptions.Scale.ToString("0.####", CultureInfo.InvariantCulture);
        }
        finally { _updating = false; }
    }

    private void OnSaveOptionChanged(object sender, RoutedEventArgs e)
    {
        if (_updating || _host == null) return;
        var o = _host.Document.WriteOptions;
        o.Encoding = EncodingBox.SelectedIndex == 1 ? new UTF8Encoding(false) : XFile.ShiftJis;
        int precision = int.TryParse(PrecisionBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) ? p : 6;
        o.Precision = Math.Clamp(precision, 1, 9);
        PrecisionBox.Text = o.Precision.ToString(CultureInfo.InvariantCulture);
        o.WriteTemplates = TemplatesBox.IsChecked == true;
        var pmx = _host.Document.PmxOptions;
        if (float.TryParse(PmxScaleBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var scale) && scale > 0)
            pmx.Scale = scale;
        else
            _host.SetStatus("PMX の単位は正の数にしてください");
        PmxScaleBox.Text = pmx.Scale.ToString("0.####", CultureInfo.InvariantCulture);
    }
}
