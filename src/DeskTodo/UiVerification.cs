using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DeskTodo.Core;

namespace DeskTodo;

internal static class UiVerification
{
    internal static async Task Run(MainWindow window, string directory)
    {
        var checks = new List<string>();
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("UI verification failed: " + name);
            checks.Add(name);
        }
        await Task.Delay(150);
        Check(window.IsVisible && window.ActualWidth > 300, "expanded window visible");
        Capture(window, Path.Combine(directory, "01-empty.png"));
        window.QuickTitle.Text = "整理今天的工作安排";
        window.AddButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(window.QuickTitle.Text == "" && window.Model.Summary.Pending == 1, "quick add saves and clears input");
        window.QuickTitle.Text = "   ";
        window.AddButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(window.Store.ReadAll().Count == 1 && window.QuickTitle.Text == "   ", "invalid add preserves input");
        window.QuickTitle.Clear();
        using (var locked = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={window.Store.DatabasePath};Pooling=False"))
        {
            locked.Open();
            using var transaction = locked.BeginTransaction();
            window.QuickTitle.Text = "保存失败后仍保留的输入";
            window.AddButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(window.QuickTitle.Text == "保存失败后仍保留的输入" && window.SaveLabel.Text == "操作未完成", "failed write keeps input and reports failure");
        }
        Check(window.Store.ReadAll().Count == 1, "failed UI write does not add phantom task");
        window.QuickTitle.Clear();
        window.Model.Add("检查昨天未完成的事项", "保留原计划日期", window.Model.Today.AddDays(-1));
        window.Model.Add("明天准备会议资料", "", window.Model.Today.AddDays(1));
        window.UpdateLayout();
        var checkbox = Descendants<CheckBox>(window.TaskSections).First(c => c.DataContext is TodoItem);
        checkbox.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        Check(window.Model.Summary.Completed == 1 && window.Model.Summary.Pending == 1, "rendered checkbox persists completion");
        Capture(window, Path.Combine(directory, "02-today.png"));
        window.UndoButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(window.Model.Summary.Pending == 2 && window.Model.Summary.Completed == 0, "undo button restores completion");
        var editItem = window.Store.ReadAll().First();
        var editor = new TaskEditor(editItem, "", window.Model.Today,
            (title, notes, date) => window.Model.Update(editItem, title, notes, date)) { Owner = window };
        _ = editor.Dispatcher.BeginInvoke(() =>
        {
            editor.TitleInput.Text = "检查第一版桌面待办的折叠、跨天和数据恢复功能";
            editor.NotesInput.Text = "多行备注\n确认关闭后仍然保存";
            Capture(editor, Path.Combine(directory, "03-editor.png"));
            editor.SaveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        });
        Check(editor.ShowDialog() == true, "task editor save closes dialog");
        Check(window.Store.ReadAll().First(t => t.Id == editItem.Id).Notes.Contains("多行"), "editor persists notes");
        window.FutureTab.IsChecked = true;
        Check(window.Model.ActiveView == "Future" && window.Model.Sections.Single().Items.Count == 1, "future tab excludes today");
        window.QuickTitle.Text = "明天检查备份";
        window.AddButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(window.Model.Sections.Single().Items.Count == 2 && window.Model.Summary.Pending == 2, "future quick add schedules tomorrow");
        Capture(window, Path.Combine(directory, "04-future.png"));
        window.TodayTab.IsChecked = true;
        window.Model.Toggle(window.Store.ReadAll().First(t => t.Id == editItem.Id));
        window.HistoryTab.IsChecked = true;
        Check(window.HistoryPicker.Visibility == Visibility.Visible && window.Model.Sections.Single().Items.Count == 1, "history tab uses actual completion date");
        Capture(window, Path.Combine(directory, "05-history.png"));
        window.Model.Delete(window.Store.ReadAll().First(t => t.Id == editItem.Id));
        window.TrashTab.IsChecked = true;
        window.UpdateLayout();
        var taskMenu = Descendants<Button>(window.TaskSections).First(b => b.ContextMenu != null);
        taskMenu.ContextMenu!.PlacementTarget = taskMenu;
        taskMenu.ContextMenu.IsOpen = true;
        Check(((MenuItem)taskMenu.ContextMenu.Items[0]).Visibility == Visibility.Collapsed &&
              ((MenuItem)taskMenu.ContextMenu.Items[2]).Visibility == Visibility.Visible, "trash context menu offers restore on right click");
        taskMenu.ContextMenu.IsOpen = false;
        var restore = Descendants<Button>(window.TaskSections).First(b => b.Name == "Restore");
        restore.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(window.Model.IsEmpty && !window.Store.ReadAll().First(t => t.Id == editItem.Id).IsDeleted, "trash restore button works");
        window.TodayTab.IsChecked = true;
        for (var i = 0; i < 12; i++) window.Model.Add($"验收事项 {i + 1}：较长的任务名称可以自动换行并保持按钮位置稳定", "", window.Model.Today);
        Capture(window, Path.Combine(directory, "06-many-tasks.png"));
        Check(Descendants<ScrollViewer>(window).Any(s => s.ScrollableHeight > 0), "long list scrolls without growing window");
        window.CollapseButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Task.Delay(60);
        Check(window.IsCollapsed && window.ActualWidth < 200 && window.Chip.IsVisible, "collapse button creates compact widget");
        Capture(window, Path.Combine(directory, "07-collapsed.png"));
        window.ShowPanel();
        Check(!window.IsCollapsed && window.Panel.IsVisible, "widget expands again");
        var originalPin = window.Topmost;
        window.PinButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(window.Topmost != originalPin && window.Store.ReadPreferences().AlwaysOnTop == window.Topmost, "pin preference saved");
        window.TogglePin();
        window.Left = -30000;
        window.Top = -30000;
        window.ShowPanel();
        var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        WindowsPlatform.GetWindowRect(handle, out var rect);
        var work = WindowsPlatform.WorkArea(handle);
        Check(rect.Left >= work.Left && rect.Top >= work.Top && rect.Right <= work.Right + 2, "offscreen window recovered");
        var settings = new SettingsWindow(window) { Owner = window };
        settings.Show();
        await Task.Delay(80);
        Capture(settings, Path.Combine(directory, "08-settings.png"));
        settings.Close();
        var backup = Path.Combine(directory, "ui-export.sqlite");
        window.Store.Backup(backup);
        Check(new TodoStore(backup).ReadAll().Count == 16, "UI tasks survive backup and reopen");
        var executable = Environment.ProcessPath!;
        if (!Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            using var second = Process.Start(new ProcessStartInfo(executable)
            { UseShellExecute = false, ArgumentList = { "--data-dir", directory } })!;
            Check(await Task.Run(() => second.WaitForExit(5000)) && second.ExitCode == 0, "second instance activates existing process");
            using var rejected = Process.Start(new ProcessStartInfo(executable)
            { UseShellExecute = false, ArgumentList = { "--verify", "--data-dir", directory } })!;
            Check(await Task.Run(() => rejected.WaitForExit(5000)) && rejected.ExitCode != 0, "verification refuses an existing database");
            Check(window.Store.ReadAll().Count == 16, "rejected verification preserves existing tasks");
        }
        File.WriteAllText(Path.Combine(directory, "ui-results.json"), JsonSerializer.Serialize(new { passed = checks.Count, checks }, new JsonSerializerOptions { WriteIndented = true }));
    }

    internal static void Capture(Window window, string path)
    {
        window.UpdateLayout();
        var width = (int)Math.Ceiling(window.ActualWidth);
        var height = (int)Math.Ceiling(window.ActualHeight);
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typed) yield return typed;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
}
