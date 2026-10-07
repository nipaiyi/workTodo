using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using DeskTodo.Core;

namespace DeskTodo;

public sealed record TaskSection(string Title, IReadOnlyList<TodoItem> Items);

public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly TodoStore store;
    private IReadOnlyList<TodoItem> tasks = Array.Empty<TodoItem>();
    private Action? undo;
    private DateOnly today;
    private DateTime historyDate = DateTime.Today;
    private string activeView = "Today";
    private string status = "";
    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<TaskSection> Sections { get; } = new();
    public DaySummary Summary { get; private set; } = new(0, 0);
    public string DateLabel => today.ToString("M月d日 dddd", CultureInfo.GetCultureInfo("zh-CN"));
    public string ProgressLabel => $"已完成 {Summary.Completed} / {Summary.Total}";
    public string CountLabel => Summary.Pending > 99 ? "99+" : Summary.Pending.ToString();
    public string FooterLabel => Summary.Pending == 0 && Summary.Completed > 0 ? "今天的安排已完成" : $"待办 {Summary.Pending} 项 · 已完成 {Summary.Completed} 项";
    public string EmptyLabel => activeView switch
    {
        "Future" => "暂无未来安排", "History" => "这一天还没有完成记录", "Trash" => "回收站是空的", _ => "今天还没有待办"
    };
    public bool IsEmpty => Sections.Count == 0;
    public bool CanUndo => undo != null;
    public string Status { get => status; set { status = value; Notify(); } }
    public string ActiveView { get => activeView; set { activeView = value; Refresh(); } }
    public DateTime HistoryDate { get => historyDate; set { historyDate = value; Refresh(); } }
    public DateOnly Today => today;

    public MainViewModel(TodoStore store)
    {
        this.store = store;
        Refresh();
    }

    public void Refresh()
    {
        today = DateOnly.FromDateTime(DateTime.Now);
        tasks = store.ReadAll();
        Summary = TodoQueries.Summary(tasks, today, TimeZoneInfo.Local);
        Sections.Clear();
        var active = tasks.Where(t => !t.IsDeleted).ToArray();
        if (activeView == "Today")
        {
            AddSection("今天", ByPriority(active.Where(t => !t.IsCompleted && t.PlannedDate == today)));
            AddSection("以前未完成", ByPriority(active.Where(t => !t.IsCompleted && t.PlannedDate < today)));
            AddSection("今日已完成", active.Where(t => TodoQueries.CompletedOn(t, today, TimeZoneInfo.Local)).OrderByDescending(t => t.CompletedAt));
        }
        else if (activeView == "Future")
        {
            foreach (var group in active.Where(t => !t.IsCompleted && t.PlannedDate > today).OrderBy(t => t.PlannedDate).GroupBy(t => t.PlannedDate))
                AddSection(group.Key.ToString("M月d日 dddd", CultureInfo.GetCultureInfo("zh-CN")), ByPriority(group));
        }
        else if (activeView == "History")
            AddSection("已完成", active.Where(t => TodoQueries.CompletedOn(t, DateOnly.FromDateTime(historyDate), TimeZoneInfo.Local)).OrderByDescending(t => t.CompletedAt));
        else AddSection("已删除", tasks.Where(t => t.IsDeleted).OrderByDescending(t => t.DeletedAt));
        foreach (var property in new[] { nameof(Summary), nameof(DateLabel), nameof(ProgressLabel), nameof(CountLabel), nameof(FooterLabel), nameof(IsEmpty), nameof(EmptyLabel), nameof(CanUndo), nameof(ActiveView), nameof(HistoryDate) }) Notify(property);
    }

    private void AddSection(string title, IEnumerable<TodoItem> items)
    {
        var list = items.ToArray();
        if (list.Length > 0) Sections.Add(new($"{title} · {list.Length}", list));
    }

    private static IOrderedEnumerable<TodoItem> ByPriority(IEnumerable<TodoItem> items)
        => items.OrderBy(t => t.Priority).ThenBy(t => t.PlannedDate).ThenBy(t => t.CreatedAt).ThenBy(t => t.Id);

    public void Add(string title, string notes, DateOnly date, TodoPriority priority = TodoPriority.Normal)
    {
        var item = TodoItem.Create(title, notes, date, DateTimeOffset.UtcNow, priority);
        store.Save(item);
        undo = () => store.Save(item with { DeletedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        Status = "已添加";
        Refresh();
    }

    public void Update(TodoItem item, string title, string notes, DateOnly date, TodoPriority priority)
        => Change(item, item with { Title = TodoItem.ValidateTitle(title), Notes = notes.Trim(), PlannedDate = date,
            Priority = TodoItem.ValidatePriority(priority), UpdatedAt = DateTimeOffset.UtcNow }, "已保存修改");

    public void Toggle(TodoItem item)
        => Change(item, item with { CompletedAt = item.IsCompleted ? null : DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow }, item.IsCompleted ? "已撤销完成" : "已完成");

    public void Delete(TodoItem item)
        => Change(item, item with { DeletedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow }, "已移入回收站");

    public void Restore(TodoItem item)
        => Change(item, item with { DeletedAt = null, UpdatedAt = DateTimeOffset.UtcNow }, "已恢复任务");

    private void Change(TodoItem previous, TodoItem next, string message)
    {
        store.Save(next);
        undo = () => store.Save(previous with { UpdatedAt = DateTimeOffset.UtcNow });
        Status = message;
        Refresh();
    }

    public void Undo()
    {
        if (undo == null) return;
        undo();
        undo = null;
        Status = "已撤销上一步";
        Refresh();
    }

    public void AfterImport(int count)
    {
        undo = null;
        Status = $"已恢复 {count} 项任务";
        Refresh();
    }

    private void Notify([System.Runtime.CompilerServices.CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new(name));
}
