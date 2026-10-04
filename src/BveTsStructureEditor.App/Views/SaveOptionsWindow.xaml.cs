using System.Windows;
using BveTsStructureEditor.App.Document;

namespace BveTsStructureEditor.App.Views;

/// <summary>保存設定（文字コード・桁数・PMX の単位）。値は変えたそばからドキュメントに入る。</summary>
public partial class SaveOptionsWindow : Window
{
    public SaveOptionsWindow(IEditorHost host)
    {
        InitializeComponent();
        Panel.Attach(host);
        Panel.Refresh(ChangeKind.All);
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        // 入力中の欄を確定させてから閉じる（LostFocus で反映する欄があるため）
        (sender as UIElement)?.Focus();
        Close();
    }
}
