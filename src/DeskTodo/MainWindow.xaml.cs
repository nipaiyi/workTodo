using System;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using DeskTodo.Core;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace DeskTodo;

public partial class MainWindow : Window
{
    internal MainViewModel Model { get; }
    internal TodoStore Store { get; }
    private WindowPreferences preferences;
    private readonly DispatcherTimer timer;
    private readonly DispatcherTimer positionTimer;
    private Forms.NotifyIcon? tray;
    private System.Drawing.Icon? trayIcon;
    private bool exitRequested, manuallyHidden, fullscreenHidden, positioned;
    private IntPtr handle;
    private DateTimeOffset lastBackup;
    internal bool IsCollapsed => preferences.Collapsed;

    public MainWindow(TodoStore store, bool startup, bool verification)
    {
        Store = store;
        Model = new(store);
        preferences = store.ReadPreferences();
        if (startup) preferences = preferences with { Collapsed = true };
        InitializeComponent();
        positionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        positionTimer.Tick += (_, _) => SavePosition();
        DataContext = Model;
        Topmost = preferences.AlwaysOnTop;
        UpdatePin();
        ApplySize();
        Loaded += (_, _) =>
        {
            handle = new WindowInteropHelper(this).Handle;
            var work = WindowsPlatform.WorkArea(handle);
            WindowsPlatform.GetWindowRect(handle, out var rect);
            WindowsPlatform.Move(handle,
                (preferences.Collapsed ? preferences.CollapsedPixelX : null) ?? preferences.PixelX ?? work.Right - rect.Width - 24,
                (preferences.Collapsed ? preferences.CollapsedPixelY : null) ?? preferences.PixelY ?? work.Top + 80);
            FitToScreen();
            positioned = true;
            SavePosition();
        };
        Closing += Window_Closing;
        LocationChanged += (_, _) => { if (positioned) { positionTimer.Stop(); positionTimer.Start(); } };
        Activated += (_, _) => Try(Model.Refresh);
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { SetCollapsed(true); e.Handled = true; }
            if (e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control &&
                Keyboard.FocusedElement is not TextBox)
            { Try(Model.Undo); e.Handled = true; }
        };
        Surface.ContextMenu = MakeContextMenu();
        if (!verification)
        {
            CreateTray();
            SystemEvents.DisplaySettingsChanged += DisplayChanged;
            SystemEvents.PowerModeChanged += PowerChanged;
            SystemEvents.TimeChanged += TimeChanged;
        }
        timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => Tick();
        if (!verification) timer.Start();
        BackupQuietly();
    }

    private void ApplySize()
    {
        Width = preferences.Collapsed ? 164 : 392;
        Height = preferences.Collapsed ? 70 : 632;
        Panel.Visibility = preferences.Collapsed ? Visibility.Collapsed : Visibility.Visible;
        Chip.Visibility = preferences.Collapsed ? Visibility.Visible : Visibility.Collapsed;
    }

    internal void SetCollapsed(bool value)
    {
        if (preferences.Collapsed == value) return;
        if (!WindowsPlatform.GetWindowRect(handle, out var old)) return;
        if (!value) preferences = preferences with { CollapsedPixelX = old.Left, CollapsedPixelY = old.Top };
        preferences = preferences with { Collapsed = value };
        ApplySize();
        UpdateLayout();
        WindowsPlatform.GetWindowRect(handle, out var current);
        // The expanded panel may be clamped upward; keep that adjustment separate from the widget's home.
        WindowsPlatform.Move(handle,
            value ? preferences.CollapsedPixelX ?? old.Right - current.Width : old.Right - current.Width,
            value ? preferences.CollapsedPixelY ?? old.Top : old.Top);
        FitToScreen();
        SavePosition();
    }

    internal void ShowPanel()
    {
        manuallyHidden = false;
        fullscreenHidden = false;
        Show();
        SetCollapsed(false);
        FitToScreen();
        Activate();
        QuickTitle.Focus();
    }

    private void HideManually()
    {
        manuallyHidden = true;
        Hide();
    }

    private void FitToScreen()
    {
        if (handle == IntPtr.Zero) return;
        var work = WindowsPlatform.WorkArea(handle);
        var scale = Math.Max(1, WindowsPlatform.GetDpiForWindow(handle)) / 96d;
        if (!preferences.Collapsed)
        {
            Height = Math.Min(632, work.Height / scale);
            Width = Math.Min(392, work.Width / scale);
        }
        WindowsPlatform.Clamp(handle);
    }

    private void SavePosition()
    {
        positionTimer.Stop();
        if (!positioned || handle == IntPtr.Zero) return;
        if (WindowsPlatform.GetWindowRect(handle, out var rect))
        {
            preferences = preferences with { PixelX = rect.Left, PixelY = rect.Top };
            if (preferences.Collapsed)
                preferences = preferences with { CollapsedPixelX = rect.Left, CollapsedPixelY = rect.Top };
        }
        Try(() => Store.SavePreferences(preferences));
    }

    private void UpdatePin()
    {
        PinButton.Foreground = (System.Windows.Media.Brush)FindResource(preferences.AlwaysOnTop ? "Accent" : "Muted");
        PinButton.ToolTip = preferences.AlwaysOnTop ? "取消置顶" : "始终置顶";
    }

    internal void TogglePin()
    {
        preferences = preferences with { AlwaysOnTop = !preferences.AlwaysOnTop };
        Topmost = preferences.AlwaysOnTop;
        UpdatePin();
        SavePosition();
    }

    private ContextMenu MakeContextMenu()
    {
        var menu = new ContextMenu();
        AddMenu("展开待办", (_, _) => ShowPanel());
        AddMenu("切换置顶", (_, _) => TogglePin());
        AddMenu("暂时隐藏", (_, _) => HideManually());
        menu.Items.Add(new Separator());
        AddMenu("设置", (_, _) => OpenSettings());
        AddMenu("退出", (_, _) => RequestExit());
        return menu;
        void AddMenu(string label, RoutedEventHandler handler)
        {
            var item = new MenuItem { Header = label };
            item.Click += handler;
            menu.Items.Add(item);
        }
    }

    private void CreateTray()
    {
        trayIcon = WindowsPlatform.CreateTrayIcon();
        tray = new Forms.NotifyIcon { Icon = trayIcon, Text = "桌面待办", Visible = true };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("展开待办", null, (_, _) => Dispatcher.Invoke(ShowPanel));
        menu.Items.Add("收起为挂件", null, (_, _) => Dispatcher.Invoke(() => { manuallyHidden = false; Show(); SetCollapsed(true); }));
        menu.Items.Add("始终置顶", null, (_, _) => Dispatcher.Invoke(TogglePin));
        menu.Items.Add("设置", null, (_, _) => Dispatcher.Invoke(OpenSettings));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => Dispatcher.Invoke(RequestExit));
        tray.ContextMenuStrip = menu;
        tray.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) Dispatcher.Invoke(ShowPanel); };
    }

    private void Tick()
    {
        var changedDay = Model.Today != DateOnly.FromDateTime(DateTime.Now);
        if (changedDay) Try(Model.Refresh);
        if (changedDay || DateTimeOffset.UtcNow - lastBackup >= TimeSpan.FromMinutes(5)) BackupQuietly();
        if (tray != null) tray.Text = $"桌面待办 · 剩余 {Model.Summary.Pending} 项";
        if (manuallyHidden || OwnedWindows.Count > 0) return;
        var shouldHide = preferences.HideInFullscreen && WindowsPlatform.IsOtherAppFullscreen(handle);
        if (shouldHide && !fullscreenHidden) { fullscreenHidden = true; Hide(); }
        else if (!shouldHide && fullscreenHidden) { fullscreenHidden = false; Show(); }
    }

    private void DisplayChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() => { FitToScreen(); SavePosition(); });
    private void PowerChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume) Dispatcher.BeginInvoke(() => { Try(Model.Refresh); FitToScreen(); });
    }
    private void TimeChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() => { TimeZoneInfo.ClearCachedData(); Try(Model.Refresh); });

    internal bool Try(Action action)
    {
        try { action(); SaveLabel.Text = "本地保存"; return true; }
        catch (ArgumentException ex)
        {
            Model.Status = ex.Message;
            return false;
        }
        catch (Exception ex)
        {
            Model.Status = "操作失败：" + ex.Message;
            SaveLabel.Text = "操作未完成";
            App.Log(Store.DataDirectory, ex);
            return false;
        }
    }

    private void BackupQuietly()
    {
        try
        {
            Store.DailyBackup(DateOnly.FromDateTime(DateTime.Now));
            lastBackup = DateTimeOffset.UtcNow;
        }
        catch (Exception ex) { Model.Status = "自动备份失败：" + ex.Message; App.Log(Store.DataDirectory, ex); lastBackup = DateTimeOffset.UtcNow; }
    }

    private void AddQuick()
    {
        var date = DateOnly.FromDateTime(DateTime.Now);
        if (Model.ActiveView == "Future") date = date.AddDays(1);
        if (Try(() => Model.Add(QuickTitle.Text, "", date, (TodoPriority)QuickPriority.SelectedIndex)))
        { QuickTitle.Clear(); QuickPriority.SelectedIndex = (int)TodoPriority.Normal; QuickTitle.Focus(); }
    }

    private void EditTask(TodoItem? item)
    {
        var initialDate = Model.ActiveView == "Future" ? DateOnly.FromDateTime(DateTime.Now).AddDays(1) : DateOnly.FromDateTime(DateTime.Now);
        var editor = new TaskEditor(item, item == null ? QuickTitle.Text : "", initialDate, (title, notes, date, priority) =>
        {
            if (item == null) Model.Add(title, notes, date, priority); else Model.Update(item, title, notes, date, priority);
        }, (TodoPriority)QuickPriority.SelectedIndex) { Owner = this };
        if (editor.ShowDialog() == true && item == null)
        { QuickTitle.Clear(); QuickPriority.SelectedIndex = (int)TodoPriority.Normal; }
    }

    private void OpenSettings()
    {
        if (!IsVisible) { manuallyHidden = false; Show(); }
        var settings = new SettingsWindow(this) { Owner = this };
        settings.ShowDialog();
    }

    internal bool HideInFullscreen => preferences.HideInFullscreen;
    internal void SetFullscreenPreference(bool value)
    {
        var next = preferences with { HideInFullscreen = value };
        Store.SavePreferences(next);
        preferences = next;
    }

    internal void RequestExit()
    {
        exitRequested = true;
        Close();
        Application.Current.Shutdown();
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!exitRequested) { e.Cancel = true; HideManually(); return; }
        timer.Stop();
        positionTimer.Stop();
        SavePosition();
        BackupQuietly();
        SystemEvents.DisplaySettingsChanged -= DisplayChanged;
        SystemEvents.PowerModeChanged -= PowerChanged;
        SystemEvents.TimeChanged -= TimeChanged;
        if (tray != null) { tray.Visible = false; tray.ContextMenuStrip?.Dispose(); tray.Dispose(); }
        trayIcon?.Dispose();
    }

    private void DragHeader(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<Button>(e.OriginalSource as DependencyObject) != null) return;
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            WindowsPlatform.GetWindowRect(handle, out var before);
            DragMove();
            FitToScreen();
            RememberPanelDrag(before);
            SavePosition();
        }
    }

    internal void RememberPanelDrag(WindowsPlatform.Rect before)
    {
        if (preferences.Collapsed || preferences.CollapsedPixelX is not { } x || preferences.CollapsedPixelY is not { } y
            || !WindowsPlatform.GetWindowRect(handle, out var after)) return;
        preferences = preferences with { CollapsedPixelX = x + after.Left - before.Left,
            CollapsedPixelY = y + after.Top - before.Top };
    }
    private void Chip_MouseDown(object sender, MouseButtonEventArgs e)
    {
        var origin = PointToScreen(e.GetPosition(this));
        DragMove();
        var end = PointToScreen(e.GetPosition(this));
        FitToScreen();
        SavePosition();
        if ((end - origin).Length < 5) ShowPanel();
    }

    internal static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current != null)
        {
            if (current is T match) return match;
            current = current is System.Windows.Media.Visual || current is System.Windows.Media.Media3D.Visual3D
                ? System.Windows.Media.VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current);
        }
        return null;
    }

    private static TodoItem? Item(object sender) => (sender as FrameworkElement)?.DataContext as TodoItem;
    private void Pin_Click(object sender, RoutedEventArgs e) => TogglePin();
    private void Collapse_Click(object sender, RoutedEventArgs e) => SetCollapsed(true);
    private void Hide_Click(object sender, RoutedEventArgs e) => HideManually();
    private void Settings_Click(object sender, RoutedEventArgs e) => OpenSettings();
    private void Add_Click(object sender, RoutedEventArgs e) => AddQuick();
    private void NewDetails_Click(object sender, RoutedEventArgs e) => Try(() => EditTask(null));
    private void QuickTitle_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { AddQuick(); e.Handled = true; } }
    private void Complete_Click(object sender, RoutedEventArgs e) { if (Item(sender) is { } item) Try(() => Model.Toggle(item)); }
    private void Delete_Click(object sender, RoutedEventArgs e) { if (Item(sender) is { } item) Try(() => Model.Delete(item)); }
    private void Restore_Click(object sender, RoutedEventArgs e) { if (Item(sender) is { } item) Try(() => Model.Restore(item)); }
    private void Edit_Click(object sender, RoutedEventArgs e) { if (Item(sender) is { } item) Try(() => EditTask(item)); }
    private void Undo_Click(object sender, RoutedEventArgs e) => Try(Model.Undo);
    private void View_Checked(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel model || sender is not RadioButton radio) return;
        Try(() => model.ActiveView = (string)radio.Tag);
        HistoryPicker.Visibility = model.ActiveView == "History" ? Visibility.Visible : Visibility.Collapsed;
        AddArea.Visibility = model.ActiveView is "Today" or "Future" ? Visibility.Visible : Visibility.Collapsed;
        QuickPlaceholder.Text = model.ActiveView == "Future" ? "添加明天要做的事…" : "添加今天要做的事…";
        AddButton.ToolTip = model.ActiveView == "Future" ? "添加到明天" : "添加到今天";
        System.Windows.Automation.AutomationProperties.SetName(AddButton, (string)AddButton.ToolTip);
    }
    private void TaskMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.ContextMenu is not { } menu || Item(sender) is not { } item) return;
        menu.PlacementTarget = button;
        menu.DataContext = item;
        menu.IsOpen = true;
    }
    private void TaskContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu || menu.PlacementTarget is not FrameworkElement target || target.DataContext is not TodoItem item) return;
        ((MenuItem)menu.Items[0]).Visibility = item.IsDeleted ? Visibility.Collapsed : Visibility.Visible;
        ((MenuItem)menu.Items[1]).Visibility = item.IsDeleted ? Visibility.Collapsed : Visibility.Visible;
        ((MenuItem)menu.Items[2]).Visibility = item.IsDeleted ? Visibility.Visible : Visibility.Collapsed;
    }
}
