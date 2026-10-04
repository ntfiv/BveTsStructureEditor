using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using BveTsStructureEditor.Core.Editing;
using BveTsStructureEditor.Core.Format;
using Microsoft.Win32;

namespace BveTsStructureEditor.App.Views;

/// <summary>フォルダー内のモデルをまとめて変換する。処理は別スレッドで、1 ファイルずつ進捗を出す。</summary>
public partial class BatchDialog : Window
{
    public sealed record ResultItem(string Name, string Message, bool Ok)
    {
        public string Mark => Ok ? "✓" : "×";
        public Brush MarkBrush => Ok ? Brushes.SeaGreen : Brushes.Firebrick;
    }

    private readonly ObservableCollection<ResultItem> _results = [];
    private readonly XWriteOptions _writeOptions;
    private CancellationTokenSource? _cancel;

    public BatchDialog(string? initialDirectory, XWriteOptions writeOptions)
    {
        InitializeComponent();
        _writeOptions = writeOptions;
        Results.ItemsSource = _results;
        InputDir.Text = initialDirectory ?? "";
        Closing += (_, e) =>
        {
            if (_cancel == null) return;
            _cancel.Cancel(); // 実行中に閉じたら、今のファイルが終わったところで止める
        };
    }

    private BatchOptions ReadOptions()
    {
        if (!float.TryParse(Scale.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var scale) || scale <= 0)
            throw new FormatException("倍率に正の数を入れてください");
        string? outDir = null;
        if (OutFolder.IsChecked == true)
        {
            if (string.IsNullOrWhiteSpace(OutputDir.Text)) throw new FormatException("出力先のフォルダーを指定してください");
            outDir = Path.GetFullPath(OutputDir.Text.Trim());
        }
        return new BatchOptions
        {
            Recursive = Recursive.IsChecked == true,
            IncludeX = IncludeX.IsChecked == true,
            IncludePmx = IncludePmx.IsChecked == true,
            Scale = scale,
            MirrorX = MirrorX.IsChecked == true,
            MirrorZ = MirrorZ.IsChecked == true,
            ReplaceFrom = ReplaceFrom.Text,
            ReplaceTo = ReplaceTo.Text,
            MakeTexturesRelative = MakeRelative.IsChecked == true,
            RemoveUnused = RemoveUnused.IsChecked == true,
            Format = Format.SelectedIndex == 1 ? BatchOutputFormat.Pmx : BatchOutputFormat.TextX,
            OutputDirectory = outDir,
            WriteOptions = _writeOptions,
        };
    }

    private void OnInputChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded && FileCount == null) return;
        var dir = InputDir.Text.Trim();
        if (!Directory.Exists(dir))
        {
            FileCount.Text = dir.Length == 0 ? "" : "フォルダーが見つかりません";
            return;
        }
        try
        {
            var o = new BatchOptions { Recursive = Recursive.IsChecked == true, IncludeX = IncludeX.IsChecked == true, IncludePmx = IncludePmx.IsChecked == true };
            FileCount.Text = $"{BatchConvert.FindFiles(dir, o).Count} ファイル";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            FileCount.Text = "フォルダーを読めません: " + ex.Message;
        }
    }

    private void OnBrowseInput(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "変換するフォルダー", InitialDirectory = Directory.Exists(InputDir.Text) ? InputDir.Text : "" };
        if (dlg.ShowDialog(this) == true) InputDir.Text = dlg.FolderName;
    }

    private void OnBrowseOutput(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "出力先のフォルダー" };
        if (dlg.ShowDialog(this) != true) return;
        OutputDir.Text = dlg.FolderName;
        OutFolder.IsChecked = true;
    }

    private void OnOutputFocus(object sender, RoutedEventArgs e) => OutFolder.IsChecked = true;

    private async void OnRun(object sender, RoutedEventArgs e)
    {
        if (_cancel != null)
        {
            _cancel.Cancel();
            return;
        }
        var root = InputDir.Text.Trim();
        BatchOptions options;
        List<string> files;
        try
        {
            if (!Directory.Exists(root)) throw new FormatException("入力フォルダーが見つかりません");
            options = ReadOptions();
            if (options.OutputDirectory != null && options.Recursive && IsInside(options.OutputDirectory, root))
                throw new FormatException("出力先を入力フォルダーの中にすると、書き出したファイルをまた読んでしまいます。別の場所を選んでください");
            files = BatchConvert.FindFiles(root, options);
        }
        catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            Summary.Text = ex.Message;
            return;
        }
        if (files.Count == 0)
        {
            Summary.Text = "対象のファイルがありません";
            return;
        }

        _results.Clear();
        _cancel = new CancellationTokenSource();
        var token = _cancel.Token;
        RunButton.Content = "中止";
        Progress.Maximum = files.Count;
        Progress.Value = 0;
        int ok = 0, failed = 0;
        try
        {
            foreach (var file in files)
            {
                if (token.IsCancellationRequested) break;
                Summary.Text = $"変換中 {Progress.Value + 1} / {files.Count}: {Path.GetRelativePath(root, file)}";
                var result = await Task.Run(() => BatchConvert.ProcessFile(file, root, options), CancellationToken.None);
                _results.Add(new ResultItem(Path.GetRelativePath(root, file), result.Message, result.Ok));
                if (result.Ok) ok++; else failed++;
                Progress.Value++;
            }
            Summary.Text = (token.IsCancellationRequested ? "中止しました。" : "完了しました。") + $"成功 {ok}・失敗 {failed}";
        }
        finally
        {
            _cancel?.Dispose();
            _cancel = null;
            RunButton.Content = "実行";
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    /// <summary><paramref name="path"/> が <paramref name="folder"/> そのものか、その中にあるか（"C:\in" と "C:\input" を取り違えない）。</summary>
    private static bool IsInside(string path, string folder)
    {
        static string Normalize(string p) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p)) + Path.DirectorySeparatorChar;
        return Normalize(path).StartsWith(Normalize(folder), StringComparison.OrdinalIgnoreCase);
    }
}
