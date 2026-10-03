using System.Windows;

namespace BveXEditor.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            var log = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "BveXEditor_error.log");
            try { System.IO.File.AppendAllText(log, $"[{DateTime.Now}] {args.Exception}\n\n"); } catch (System.IO.IOException) { }
            MessageBox.Show($"予期しないエラーが起きました。\n\n{args.Exception.Message}\n\n詳細: {log}", "BVE X Editor",
                MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        var window = new MainWindow(e.Args.FirstOrDefault());
        window.Show();
    }
}
