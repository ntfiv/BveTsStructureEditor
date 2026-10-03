using System.Windows;
using System.Windows.Input;

namespace BveXEditor.App.Views;

/// <summary>視点の動かし方（<see cref="NavigationSettings"/>）を Helix のマウス操作に割り当てる。</summary>
public partial class ViewportView
{
    private bool _invertZoom;
    private bool _wheelHooked;

    private static MouseGesture Gesture(MouseAction action, ModifierKeys modifiers = ModifierKeys.None) => new(action, modifiers);

    /// <summary>
    /// 「割り当てなし」。Helix は一度入れたジェスチャーを null に戻せない（InputBinding が例外を出す）ので、
    /// 押されることのない MouseAction.None を入れておく。
    /// </summary>
    private static MouseGesture Unassigned => new(MouseAction.None);

    public void ApplyNavigation(NavigationSettings s)
    {
        (MouseGesture rotate, MouseGesture pan, MouseGesture pan2, MouseGesture zoom) = s.Style switch
        {
            NavigationStyle.Blender => (Gesture(MouseAction.MiddleClick), Gesture(MouseAction.MiddleClick, ModifierKeys.Shift), Unassigned, Gesture(MouseAction.MiddleClick, ModifierKeys.Control)),
            NavigationStyle.SketchUp => (Gesture(MouseAction.MiddleClick), Gesture(MouseAction.MiddleClick, ModifierKeys.Shift), Unassigned, Unassigned),
            NavigationStyle.Fusion => (Gesture(MouseAction.MiddleClick, ModifierKeys.Shift), Gesture(MouseAction.MiddleClick), Unassigned, Unassigned),
            _ => (Gesture(MouseAction.RightClick), Gesture(MouseAction.MiddleClick), Gesture(MouseAction.RightClick, ModifierKeys.Shift), Gesture(MouseAction.RightClick, ModifierKeys.Control)),
        };
        View.RotateGesture = rotate;
        View.RotateGesture2 = Unassigned;
        View.PanGesture = pan;
        View.PanGesture2 = pan2;
        View.ZoomGesture = zoom;
        View.ZoomGesture2 = Unassigned;
        // 中ダブルクリックでカメラが初期位置に戻ると、中ボタンで回す方式で誤爆する（全体表示は F キー）
        View.ResetCameraGesture = Unassigned;
        // WASD で視点が動くと、アプリのキー（W/S/D）が拾わない A だけ素通りしてしまう
        View.IsMoveEnabled = false;
        View.RotateAroundMouseDownPoint = s.AroundMouse;
        View.ZoomAroundMouseDownPoint = s.AroundMouse;

        _invertZoom = s.InvertZoom;
        if (!_wheelHooked)
        {
            View.PreviewMouseWheel += OnPreviewWheel;
            _wheelHooked = true;
        }

        string drag = s.Style switch
        {
            NavigationStyle.Blender or NavigationStyle.SketchUp => "中ドラッグ: 回転 / Shift+中ドラッグ: 移動",
            NavigationStyle.Fusion => "Shift+中ドラッグ: 回転 / 中ドラッグ: 移動",
            _ => "右ドラッグ: 回転 / 中ドラッグ: 移動",
        };
        NavHint.Text = $"{drag} / ホイール: ズーム（ここにマウスで操作一覧）";
        NavHintTip.Text =
            "左クリック / ドラッグ: 選択（Shift で追加、Ctrl で反転）\n" +
            "ギズモをドラッグ: 移動・回転・拡大（Ctrl でスナップ、移動中の Shift で頂点に吸着、Esc で取り消し）\n" +
            $"視点（{NavigationSettings.DisplayName(s.Style)}）: {NavigationSettings.Describe(s.Style)} / ホイール: ズーム{(s.InvertZoom ? "（向きを反転）" : "")}\n" +
            "Q/E/R: 選択の種類  G/T/S: ギズモ  M: 左右対称  O: 平行投影  D: 寸法  W: ワイヤー  F: 全体表示  1/3/7/5: 視点\n" +
            "視点の動かし方は 表示 > 視点の操作 で変えられます";
    }

    /// <summary>ズームの向きを逆にする: 符号を逆にしたホイールを Helix のカメラ操作に渡し直す。</summary>
    private void OnPreviewWheel(object sender, MouseWheelEventArgs e)
    {
        if (!_invertZoom || e.Handled || IsOverHelixWidget(e)) return;
        e.Handled = true;
        var flipped = new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, -e.Delta) { RoutedEvent = UIElement.MouseWheelEvent };
        View.CameraController?.RaiseEvent(flipped);
    }
}
