using System.Windows;

namespace BveXEditor.App.Views;

/// <summary>BVE 互換チェックの一覧を出す、閉じずに編集を続けられるウィンドウ。</summary>
public partial class CheckWindow : Window
{
    public CheckWindow(IEditorHost host)
    {
        InitializeComponent();
        Panel.Attach(host);
    }
}
