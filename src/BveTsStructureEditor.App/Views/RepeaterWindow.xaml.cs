using System.Windows;
using BveTsStructureEditor.App.Rendering;

namespace BveTsStructureEditor.App.Views;

/// <summary>Repeater 用の分割・書き出し・並べたプレビューの窓。開いたまま編集を続けられる。</summary>
public partial class RepeaterWindow : Window
{
    public RepeaterWindow(IEditorHost host, RepeaterPreviewRenderer preview)
    {
        InitializeComponent();
        Panel.Attach(host, preview);
        // 閉じたら 3D ビューに重ねた表示も消す
        Closed += (_, _) => Panel.ClearPreview();
    }
}
