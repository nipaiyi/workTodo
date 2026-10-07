using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
        Check(window.Store.ReadAll().Single().Priority == TodoPriority.Normal && window.QuickPriority.SelectedIndex == 1,
            "quick add defaults to normal priority");
        window.QuickTitle.Text = "   ";
        window.AddButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(window.Store.ReadAll().Count == 1 && window.QuickTitle.Text == "   ", "invalid add preserves input");
        window.QuickTitle.Clear();
        using (var locked = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={window.Store.DatabasePath};Pooling=False"))
        {
            locked.Open();
            using var transaction = locked.BeginTransaction();
            window.QuickTitle.Text = "保存失败后仍保留的输入";
            window.QuickPriority.SelectedIndex = 0;
            window.AddButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(window.QuickTitle.Text == "保存失败后仍保留的输入" && window.SaveLabel.Text == "操作未完成", "failed write keeps input and reports failure");
            Check(window.QuickPriority.SelectedIndex == 0, "failed write preserves selected priority");
        }
        Check(window.Store.ReadAll().Count == 1, "failed UI write does not add phantom task");
        window.QuickTitle.Clear();
        window.Model.Add("检查昨天未完成的事项", "保留原计划日期", window.Model.Today.AddDays(-1), TodoPriority.Urgent);
        window.Model.Add("明天准备会议资料", "", window.Model.Today.AddDays(1));
        window.UpdateLayout();
        var checkbox = Descendants<CheckBox>(window.TaskSections).First(c => c.DataContext is TodoItem);
        checkbox.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        Check(window.Model.Summary.Completed == 1 && window.Model.Summary.Pending == 1, "rendered checkbox persists completion");
        Capture(window, Path.Combine(directory, "02-today.png"));
        window.UndoButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(window.Model.Summary.Pending == 2 && window.Model.Summary.Completed == 0, "undo button restores completion");
        var editItem = window.Store.ReadAll().First();
        var newEditor = new TaskEditor(null, "", window.Model.Today, (_, _, _, _) => { });
        Check(newEditor.PriorityInput.SelectedIndex == 1, "new detail editor defaults to normal priority");
        newEditor.Close();
        var urgentEditor = new TaskEditor(null, "", window.Model.Today, (_, _, _, _) => { }, TodoPriority.Urgent);
        Check(urgentEditor.PriorityInput.SelectedIndex == 0, "new detail editor inherits selected quick priority");
        urgentEditor.Close();
        var editor = new TaskEditor(editItem, "", window.Model.Today,
            (title, notes, date, priority) => window.Model.Update(editItem, title, notes, date, priority)) { Owner = window };
        Check(editor.PriorityInput.SelectedIndex == (int)editItem.Priority, "editor loads existing priority");
        _ = editor.Dispatcher.BeginInvoke(() =>
        {
            editor.TitleInput.Text = "检查第一版桌面待办的折叠、跨天和数据恢复功能";
            editor.NotesInput.Text = "多行备注\n确认关闭后仍然保存";
            editor.PriorityInput.SelectedIndex = 2;
            Capture(editor, Path.Combine(directory, "03-editor.png"));
            editor.SaveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        });
        Check(editor.ShowDialog() == true, "task editor save closes dialog");
        Check(window.Store.ReadAll().First(t => t.Id == editItem.Id).Notes.Contains("多行"), "editor persists notes");
        Check(window.Store.ReadAll().Single(t => t.Id == editItem.Id).Priority == TodoPriority.Relaxed, "editor persists changed priority");
        window.Model.Undo();
        var undone = window.Store.ReadAll().Single(t => t.Id == editItem.Id);
        Check((undone with { UpdatedAt = editItem.UpdatedAt }) == editItem,
            "undo edit restores previous content and priority");
        window.Model.Update(window.Store.ReadAll().Single(t => t.Id == editItem.Id), "检查第一版桌面待办的折叠、跨天和数据恢复功能",
            "多行备注\n确认关闭后仍然保存", editItem.PlannedDate, TodoPriority.Relaxed);
        window.FutureTab.IsChecked = true;
        Check(window.Model.ActiveView == "Future" && window.Model.Sections.Single().Items.Count == 1, "future tab excludes today");
        window.QuickTitle.Text = "明天检查备份";
        window.AddButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(window.Model.Sections.Single().Items.Count == 2 && window.Model.Summary.Pending == 2, "future quick add schedules tomorrow");
        Check(window.Model.Sections.Single().Items.First().Priority == TodoPriority.Urgent && window.QuickPriority.SelectedIndex == 1,
            "future quick add uses selected priority, sorts urgent first and resets default");
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
        Check(window.Store.ReadAll().Single(t => t.Id == editItem.Id).Priority == TodoPriority.Relaxed,
            "completion, deletion and restore preserve priority");
        window.TodayTab.IsChecked = true;
        for (var i = 0; i < 12; i++) window.Model.Add($"验收事项 {i + 1}：较长的任务名称可以自动换行并保持按钮位置稳定", "", window.Model.Today, (TodoPriority)(i % 3));
        var ordered = window.Model.Sections.First().Items.Select(t => t.Priority).ToArray();
        Check(ordered.SequenceEqual(ordered.Order()), "today pending tasks sort by urgency");
        window.UpdateLayout();
        Check(new[] { "急急急", "抓点紧", "不慌" }.All(label => Descendants<TextBlock>(window.TaskSections).Any(t => t.Name == "PriorityText" && t.Text == label)),
            "rendered task rows show all three priority labels");
        var samePriority = window.Model.Sections.First().Items.Where(t => t.Priority == TodoPriority.Normal).Select(t => t.Id).ToArray();
        window.Model.Refresh();
        Check(window.Model.Sections.First().Items.Where(t => t.Priority == TodoPriority.Normal).Select(t => t.Id).SequenceEqual(samePriority),
            "same-priority ordering stays stable on refresh");
        Capture(window, Path.Combine(directory, "06-many-tasks.png"));
        Check(Descendants<ScrollViewer>(window).Any(s => s.ScrollableHeight > 0), "long list scrolls without growing window");
        window.CollapseButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Task.Delay(60);
        Check(window.IsCollapsed && window.ActualWidth < 200 && window.Chip.IsVisible, "collapse button creates compact widget");
        Capture(window, Path.Combine(directory, "07-collapsed.png"));
        var gestureHandle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        WindowsPlatform.GetWindowRect(gestureHandle, out var gestureHome);
        var chipLabel = window.Chip.Children.OfType<TextBlock>().First();
        Check(!window.TryToggleFromDoubleClick(chipLabel, MouseButton.Left, 1) && window.IsCollapsed,
            "single left click does not expand widget");
        Check(!window.TryToggleFromDoubleClick(chipLabel, MouseButton.Right, 2) && window.IsCollapsed,
            "right double click does not toggle widget");
        Check(window.TryToggleFromDoubleClick(chipLabel, MouseButton.Left, 2) && !window.IsCollapsed && window.Panel.IsVisible,
            "left double click on widget content expands panel");
        Check(!window.TryToggleFromDoubleClick(window.QuickTitle, MouseButton.Left, 2) && !window.IsCollapsed,
            "double click in task input does not collapse panel");
        Check(!window.TryToggleFromDoubleClick(window.PinButton, MouseButton.Left, 2) && !window.IsCollapsed,
            "double click on header button does not collapse panel");
        var taskText = Descendants<TextBlock>(window.TaskSections).First(t => t.Name == "TaskTitle");
        Check(!window.TryToggleFromDoubleClick(taskText, MouseButton.Left, 2) && !window.IsCollapsed,
            "double click on task text does not collapse panel");
        var headerText = Descendants<TextBlock>(window.PanelHeader).First();
        Check(window.TryToggleFromDoubleClick(headerText, MouseButton.Left, 2) && window.IsCollapsed,
            "left double click on panel title collapses widget");
        WindowsPlatform.GetWindowRect(gestureHandle, out var gestureReturned);
        Check(gestureReturned.Left == gestureHome.Left && gestureReturned.Top == gestureHome.Top,
            "double click toggle preserves original widget position");
        var repeated = true;
        for (var i = 0; i < 4; i++)
            repeated &= window.TryToggleFromDoubleClick(chipLabel, MouseButton.Left, 2)
                && window.TryToggleFromDoubleClick(window.PanelHeader, MouseButton.Left, 2);
        Check(repeated && window.IsCollapsed, "repeated double click toggles work without extra transitions");
        var expandMenu = window.Surface.ContextMenu!.Items.OfType<MenuItem>().Single(item => (string)item.Header == "展开待办");
        expandMenu.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Check(!window.IsCollapsed, "right click expand menu remains available alongside double click");
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
        window.SetCollapsed(true);
        window.UpdateLayout();
        WindowsPlatform.GetWindowRect(handle, out var chipRect);
        WindowsPlatform.Move(handle, work.Left + 24, work.Bottom - chipRect.Height - 12);
        await Task.Delay(60);
        WindowsPlatform.GetWindowRect(handle, out var leftHome);
        window.ShowPanel();
        await Task.Delay(60);
        WindowsPlatform.GetWindowRect(handle, out var expanded);
        Check(expanded.Top < leftHome.Top && expanded.Left >= work.Left && expanded.Top >= work.Top
            && expanded.Right <= work.Right + 2 && expanded.Bottom <= work.Bottom + 2,
            "bottom-left expansion stays inside work area");
        window.SetCollapsed(true);
        await Task.Delay(60);
        WindowsPlatform.GetWindowRect(handle, out var restoredHome);
        Check(restoredHome.Left == leftHome.Left && restoredHome.Top == leftHome.Top,
            "collapse returns to exact bottom-left widget position");
        for (var i = 0; i < 4; i++) { window.ShowPanel(); window.SetCollapsed(true); }
        WindowsPlatform.GetWindowRect(handle, out restoredHome);
        Check(restoredHome.Left == leftHome.Left && restoredHome.Top == leftHome.Top,
            "repeated expand-collapse cycles do not drift");
        WindowsPlatform.Move(handle, work.Right - restoredHome.Width - 24, work.Bottom - restoredHome.Height - 12);
        WindowsPlatform.GetWindowRect(handle, out var rightHome);
        window.ShowPanel();
        window.TogglePin();
        window.TogglePin();
        window.ShowPanel();
        var savedHome = window.Store.ReadPreferences();
        Check(savedHome.CollapsedPixelX == rightHome.Left && savedHome.CollapsedPixelY == rightHome.Top,
            "pin and repeated show preserve remembered widget position");
        window.SetCollapsed(true);
        WindowsPlatform.GetWindowRect(handle, out restoredHome);
        Check(restoredHome.Left == rightHome.Left && restoredHome.Top == rightHome.Top,
            "moving widget establishes a new bottom-right home");
        Capture(window, Path.Combine(directory, "10-bottom-widget.png"));
        window.ShowPanel();
        WindowsPlatform.GetWindowRect(handle, out var beforeDrag);
        WindowsPlatform.Move(handle, beforeDrag.Left - 24, beforeDrag.Top - 24);
        WindowsPlatform.Clamp(handle);
        WindowsPlatform.GetWindowRect(handle, out var afterDrag);
        window.RememberPanelDrag(beforeDrag);
        window.SetCollapsed(true);
        WindowsPlatform.GetWindowRect(handle, out restoredHome);
        Check(restoredHome.Left == rightHome.Left + afterDrag.Left - beforeDrag.Left
            && restoredHome.Top == rightHome.Top + afterDrag.Top - beforeDrag.Top,
            "deliberate panel drag moves widget home by the same distance");
        window.ShowPanel();
        var savedExpanded = window.Store.ReadPreferences();
        var startupWindow = new MainWindow(window.Store, true, true) { Owner = window };
        startupWindow.Show();
        await Task.Delay(80);
        var startupHandle = new System.Windows.Interop.WindowInteropHelper(startupWindow).Handle;
        WindowsPlatform.GetWindowRect(startupHandle, out var startupRect);
        Check(startupWindow.IsCollapsed && startupRect.Left == restoredHome.Left && startupRect.Top == restoredHome.Top,
            "startup widget restores its saved home even when last exit was expanded");
        startupWindow.Hide();
        window.Store.SavePreferences(savedExpanded);
        var restartedWindow = new MainWindow(window.Store, false, true) { Owner = window };
        restartedWindow.Show();
        await Task.Delay(80);
        restartedWindow.SetCollapsed(true);
        var restartedHandle = new System.Windows.Interop.WindowInteropHelper(restartedWindow).Handle;
        WindowsPlatform.GetWindowRect(restartedHandle, out var restartedRect);
        Check(restartedRect.Left == restoredHome.Left && restartedRect.Top == restoredHome.Top,
            "normal restart retains collapsed home independently from panel position");
        restartedWindow.Hide();
        var offscreenStore = new TodoStore(Path.Combine(directory, "offscreen-home", "tasks.sqlite"));
        offscreenStore.SavePreferences(new WindowPreferences(rect.Left, rect.Top,
            CollapsedPixelX: -30000, CollapsedPixelY: -30000));
        var offscreenWindow = new MainWindow(offscreenStore, false, true) { Owner = window };
        offscreenWindow.Show();
        await Task.Delay(80);
        offscreenWindow.SetCollapsed(true);
        var offscreenHandle = new System.Windows.Interop.WindowInteropHelper(offscreenWindow).Handle;
        WindowsPlatform.GetWindowRect(offscreenHandle, out var clampedHome);
        var currentWork = WindowsPlatform.WorkArea(offscreenHandle);
        Check(clampedHome.Left >= currentWork.Left && clampedHome.Top >= currentWork.Top
            && clampedHome.Right <= currentWork.Right && clampedHome.Bottom <= currentWork.Bottom,
            "unavailable widget home is clamped onto an available monitor");
        offscreenWindow.Hide();
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
        var priorityStore = new TodoStore(Path.Combine(directory, "priority-preview", "tasks.sqlite"));
        priorityStore.Save(TodoItem.Create("安排本周工作", "", window.Model.Today, DateTimeOffset.UtcNow));
        priorityStore.Save(TodoItem.Create("整理资料", "", window.Model.Today, DateTimeOffset.UtcNow, TodoPriority.Relaxed));
        priorityStore.Save(TodoItem.Create("提交今日截止的材料", "", window.Model.Today, DateTimeOffset.UtcNow, TodoPriority.Urgent));
        var preview = new MainWindow(priorityStore, false, true) { Owner = window };
        preview.Show();
        await Task.Delay(80);
        Capture(preview, Path.Combine(directory, "09-priorities.png"));
        var badges = Descendants<TextBlock>(preview.TaskSections).Where(t => t.Name == "PriorityText").ToArray();
        Check(badges.Length == 3 && badges.All(t => t.TransformToAncestor(preview).Transform(new Point(0, 0)).Y
            + t.ActualHeight < preview.ActualHeight - 80), "all priority labels fit in compact panel");
        var urgentItem = priorityStore.ReadAll().Single(t => t.Priority == TodoPriority.Urgent);
        var cancelled = new TaskEditor(urgentItem, "", window.Model.Today,
            (title, notes, date, priority) => preview.Model.Update(urgentItem, title, notes, date, priority)) { Owner = preview };
        Check(cancelled.PriorityInput.SelectedIndex == 0, "editing urgent task does not reset its priority");
        _ = cancelled.Dispatcher.BeginInvoke(() => { cancelled.PriorityInput.SelectedIndex = 2; cancelled.DialogResult = false; });
        Check(cancelled.ShowDialog() == false && priorityStore.ReadAll().Single(t => t.Id == urgentItem.Id) == urgentItem,
            "cancelling priority edit leaves task untouched");
        preview.Hide();
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
