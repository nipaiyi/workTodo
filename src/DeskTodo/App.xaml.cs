using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows;
using DeskTodo.Core;

namespace DeskTodo;

public partial class App : Application
{
    private Mutex? mutex;
    private bool ownsMutex;
    private EventWaitHandle? showEvent;
    private RegisteredWaitHandle? registration;
    private string dataDirectory = "";
    private bool verification;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        verification = e.Args.Contains("--verify");
        var dataIndex = Array.IndexOf(e.Args, "--data-dir");
        dataDirectory = Path.GetFullPath(dataIndex >= 0 && dataIndex + 1 < e.Args.Length
            ? e.Args[dataIndex + 1] : verification
                ? Path.Combine(AppContext.BaseDirectory, "verification", Guid.NewGuid().ToString("N"))
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeskTodo"));
        try
        {
            if (verification && File.Exists(Path.Combine(dataDirectory, "tasks.sqlite")))
                throw new InvalidOperationException("界面验证必须使用新的测试数据目录，现有任务未被修改。");
            var suffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dataDirectory.ToUpperInvariant())))[..24];
            mutex = new Mutex(true, @"Local\DeskTodo-" + suffix, out ownsMutex);
            showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\DeskTodo-Show-" + suffix);
            if (!ownsMutex) { showEvent.Set(); Shutdown(); return; }
            var store = new TodoStore(Path.Combine(dataDirectory, "tasks.sqlite"));
            var window = new MainWindow(store, e.Args.Contains("--startup"), verification);
            MainWindow = window;
            if (showEvent != null) registration = ThreadPool.RegisterWaitForSingleObject(showEvent,
                (_, _) => Dispatcher.BeginInvoke(window.ShowPanel), null, Timeout.Infinite, false);
            DispatcherUnhandledException += (_, args) =>
            {
                Log(dataDirectory, args.Exception);
                if (verification) { Shutdown(1); return; }
                MessageBox.Show("操作未完成：" + args.Exception.Message, "桌面待办", MessageBoxButton.OK, MessageBoxImage.Warning);
                args.Handled = true;
            };
            window.Show();
            if (verification) window.Dispatcher.BeginInvoke(async () =>
            {
                try { await UiVerification.Run(window, dataDirectory); window.RequestExit(); }
                catch (Exception ex) { Log(dataDirectory, ex); window.RequestExit(); Environment.ExitCode = 1; }
            });
        }
        catch (Exception ex)
        {
            Log(dataDirectory, ex);
            if (!verification) MessageBox.Show("无法启动，原始数据未被清空。\n" + ex.Message, "桌面待办", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    internal static void Log(string directory, Exception exception)
    {
        try { Directory.CreateDirectory(directory); File.AppendAllText(Path.Combine(directory, "errors.log"), $"{DateTimeOffset.Now:O} {exception}\n"); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        registration?.Unregister(null);
        showEvent?.Dispose();
        if (ownsMutex) mutex?.ReleaseMutex();
        mutex?.Dispose();
        base.OnExit(e);
    }
}
