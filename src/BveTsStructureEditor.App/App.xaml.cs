using System.Windows;

namespace BveTsStructureEditor.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        MigrateLegacySettings();
        DispatcherUnhandledException += (_, args) =>
        {
            var log = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "BveTsStructureEditor_error.log");
            try { System.IO.File.AppendAllText(log, $"[{DateTime.Now}] {args.Exception}\n\n"); } catch (System.IO.IOException) { }
            MessageBox.Show($"予期しないエラーが起きました。\n\n{args.Exception.Message}\n\n詳細: {log}", "BveTs Structure Editor",
                MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        var window = new MainWindow(e.Args.FirstOrDefault());
        window.Show();
    }

    /// <summary>
    /// 改名前（BveXEditor）の設定・地図キャッシュのフォルダーがあり、新しい名前のフォルダーがまだ無ければ移す。
    /// 移せなくても起動は続ける（設定が初期値に戻るだけ）。
    /// </summary>
    private static void MigrateLegacySettings()
    {
        foreach (var root in new[] { Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.LocalApplicationData })
        {
            var baseDir = Environment.GetFolderPath(root);
            var oldDir = System.IO.Path.Combine(baseDir, "BveXEditor");
            var newDir = System.IO.Path.Combine(baseDir, "BveTsStructureEditor");
            if (!System.IO.Directory.Exists(oldDir) || System.IO.Directory.Exists(newDir)) continue;
            try { System.IO.Directory.Move(oldDir, newDir); }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { }
        }
    }
}
