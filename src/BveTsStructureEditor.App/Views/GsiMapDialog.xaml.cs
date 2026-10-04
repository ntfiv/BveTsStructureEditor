using System.Globalization;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using BveTsStructureEditor.App.Geo;
using BveTsStructureEditor.Core.Geo;

namespace BveTsStructureEditor.App.Views;

/// <summary>地点・種類・範囲を決めて地理院タイルを取得する。成功したら <see cref="Result"/> に画像と実寸が入る。</summary>
public partial class GsiMapDialog : Window
{
    private CancellationTokenSource? _cancel;
    private readonly bool _ready;

    public GsiMapResult? Result { get; private set; }
    public GsiLayer Layer => GsiLayer.All[Math.Max(0, LayerBox.SelectedIndex)];
    public double Lat { get; private set; }
    public double Lon { get; private set; }
    public double SizeMeters => double.Parse((string)((ComboBoxItem)SizeBox.SelectedItem).Tag, CultureInfo.InvariantCulture);

    public GsiMapDialog(string? initialPlace)
    {
        InitializeComponent();
        foreach (var l in GsiLayer.All) LayerBox.Items.Add(l.Label);
        LayerBox.SelectedIndex = 0;
        PlaceBox.Text = initialPlace ?? "";
        _ready = true;
        UpdatePlan();
        Loaded += (_, _) => { PlaceBox.Focus(); PlaceBox.SelectAll(); };
        Closing += (_, _) => _cancel?.Cancel(); // 取得中に閉じたら止める
    }

    private void OnInputChanged(object sender, RoutedEventArgs e)
    {
        if (_ready) UpdatePlan();
    }

    private bool UpdatePlan()
    {
        ErrorText.Visibility = Visibility.Collapsed;
        if (!LatLonParser.TryParse(PlaceBox.Text, out var lat, out var lon))
        {
            PlanInfo.Text = string.IsNullOrWhiteSpace(PlaceBox.Text) ? "中心の地点を入れてください" : "緯度経度が読めません";
            FetchButton.IsEnabled = false;
            return false;
        }
        Lat = lat;
        Lon = lon;
        var plan = GsiTileFetcher.PlanFor(lat, lon, SizeMeters, Layer);
        PlanInfo.Text = $"緯度 {lat:0.000000}、経度 {lon:0.000000} ／ ズーム {plan.Zoom}・タイル {plan.TileCount} 枚・" +
                        $"画像 {plan.CropWidth}×{plan.CropHeight}px（1px ≒ {plan.MetersPerPixel:0.###} m）";
        FetchButton.IsEnabled = plan.TileCount <= GsiTileFetcher.MaxTiles;
        if (!FetchButton.IsEnabled) ShowError($"タイルが多すぎます（上限 {GsiTileFetcher.MaxTiles} 枚）。範囲を小さくしてください");
        return FetchButton.IsEnabled;
    }

    private void ShowError(string text)
    {
        ErrorText.Text = text;
        ErrorText.Visibility = Visibility.Visible;
    }

    private async void OnFetch(object sender, RoutedEventArgs e)
    {
        if (!UpdatePlan()) return;
        _cancel = new CancellationTokenSource();
        SetBusy(true);
        try
        {
            var progress = new Progress<string>(s => Progress.Text = s);
            Result = await GsiTileFetcher.FetchAsync(Lat, Lon, SizeMeters, Layer, progress, _cancel.Token);
            DialogResult = true;
        }
        catch (OperationCanceledException)
        {
            Progress.Text = "取得を取り消しました";
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or System.IO.IOException or System.IO.InvalidDataException or NotSupportedException)
        {
            ShowError("取得できませんでした: " + ex.Message + "（インターネットにつながっているか確認してください）");
        }
        finally
        {
            _cancel?.Dispose();
            _cancel = null;
            if (IsLoaded) SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        FetchButton.IsEnabled = !busy;
        PlaceBox.IsEnabled = LayerBox.IsEnabled = SizeBox.IsEnabled = !busy;
        CancelButton.Content = busy ? "取得を中止" : "キャンセル";
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        if (_cancel != null) _cancel.Cancel();
        else DialogResult = false;
    }
}
