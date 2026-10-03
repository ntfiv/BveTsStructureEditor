using System.IO;
using System.Windows;

namespace BveXEditor.App.Views;

/// <summary>モデルファイルをドロップしたときに「追加」か「開く」かを選ぶ。</summary>
public partial class DropChoiceDialog : Window
{
    public enum Choice { Cancel, Add, Open }

    public Choice Result { get; private set; } = Choice.Cancel;
    public bool PlaceAtDropPoint => PlaceAtDrop.IsChecked == true;

    public DropChoiceDialog(IReadOnlyList<string> files, string currentName, bool canPlaceAtDrop)
    {
        InitializeComponent();
        Message.Text = files.Count == 1
            ? $"「{Path.GetFileName(files[0])}」をどう読み込みますか？"
            : $"{files.Count} 個のモデルをどう読み込みますか？";
        FileList.Text = (files.Count > 1 ? string.Join("、", files.Select(Path.GetFileName)) + "\n" : "") +
                        $"今開いているファイル: {currentName}";
        PlaceAtDrop.IsEnabled = canPlaceAtDrop;
        if (!canPlaceAtDrop) PlaceAtDrop.ToolTip = "3D ビューの地面の上にドロップしたときに選べます";
        // 複数ファイルを「開く」ときは先頭だけ開くことを明示する
        if (files.Count > 1) OpenButton.Content = "先頭を新しいファイルとして開く";
    }

    private void OnAdd(object sender, RoutedEventArgs e)
    {
        Result = Choice.Add;
        DialogResult = true;
    }

    private void OnOpen(object sender, RoutedEventArgs e)
    {
        Result = Choice.Open;
        DialogResult = true;
    }
}
