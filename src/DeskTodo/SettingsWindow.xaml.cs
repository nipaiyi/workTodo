using System;
using System.Windows;
using Microsoft.Win32;

namespace DeskTodo;

public partial class SettingsWindow : Window
{
    private readonly MainWindow main;
    public SettingsWindow(MainWindow main)
    {
        InitializeComponent();
        this.main = main;
        TopmostToggle.IsChecked = main.Topmost;
        FullscreenToggle.IsChecked = main.HideInFullscreen;
        DataPathLabel.Text = main.Store.DataDirectory;
        Attempt(() => StartupToggle.IsChecked = WindowsPlatform.StartsWithWindows());
    }

    private bool Attempt(Action action)
    {
        try { action(); StatusLabel.Text = ""; return true; }
        catch (Exception ex) { StatusLabel.Text = "操作失败：" + ex.Message; App.Log(main.Store.DataDirectory, ex); return false; }
    }
    private void Topmost_Click(object sender, RoutedEventArgs e)
    {
        Attempt(main.TogglePin);
        TopmostToggle.IsChecked = main.Topmost;
    }
    private void Fullscreen_Click(object sender, RoutedEventArgs e)
    {
        Attempt(() => main.SetFullscreenPreference(FullscreenToggle.IsChecked == true));
        FullscreenToggle.IsChecked = main.HideInFullscreen;
    }
    private void Startup_Click(object sender, RoutedEventArgs e)
    {
        if (!Attempt(() => WindowsPlatform.SetStartsWithWindows(StartupToggle.IsChecked == true)))
            StartupToggle.IsChecked = !StartupToggle.IsChecked;
    }
    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Title = "导出待办备份", Filter = "待办备份 (*.sqlite)|*.sqlite", FileName = $"桌面待办-{DateTime.Now:yyyy-MM-dd}.sqlite" };
        if (dialog.ShowDialog(this) == true && Attempt(() => main.Store.Backup(dialog.FileName))) StatusLabel.Text = "备份已导出";
    }
    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "选择待办备份", Filter = "待办备份 (*.sqlite)|*.sqlite" };
        if (dialog.ShowDialog(this) != true) return;
        if (MessageBox.Show(this, "恢复会替换当前所有任务，现有数据会先自动备份。是否继续？", "恢复备份", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        if (Attempt(() => main.Model.AfterImport(main.Store.Restore(dialog.FileName)))) StatusLabel.Text = "任务已恢复，窗口设置保持当前配置";
    }
    private void Folder_Click(object sender, RoutedEventArgs e) => Attempt(() => WindowsPlatform.OpenFolder(main.Store.DataDirectory));
    private void Exit_Click(object sender, RoutedEventArgs e) { Close(); main.RequestExit(); }
}
