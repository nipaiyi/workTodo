using System.Globalization;

namespace DeskTodo.Core;

public enum TodoPriority { Urgent = 0, Normal = 1, Relaxed = 2 }

public sealed record TodoItem(
    Guid Id, string Title, string Notes, DateOnly PlannedDate,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    DateTimeOffset? CompletedAt = null, DateTimeOffset? DeletedAt = null,
    TodoPriority Priority = TodoPriority.Normal)
{
    public bool IsCompleted => CompletedAt.HasValue;
    public bool IsDeleted => DeletedAt.HasValue;
    public string DateLabel => PlannedDate.ToString("M月d日", CultureInfo.GetCultureInfo("zh-CN"));
    public string PriorityLabel => Priority switch
    {
        TodoPriority.Urgent => "急急急", TodoPriority.Normal => "抓点紧", TodoPriority.Relaxed => "不慌",
        _ => throw new InvalidDataException("任务等级无效。")
    };
    public string AccessibleTitle => (IsCompleted ? "已完成：" : "未完成：") + Title + "，" + PriorityLabel;

    public static TodoItem Create(string title, string notes, DateOnly date, DateTimeOffset now,
        TodoPriority priority = TodoPriority.Normal)
        => new(Guid.NewGuid(), ValidateTitle(title), notes.Trim(), date, now, now,
            Priority: ValidatePriority(priority));

    public static TodoPriority ValidatePriority(TodoPriority priority)
        => Enum.IsDefined(priority) ? priority : throw new ArgumentException("请选择有效的任务等级。");

    public static string ValidateTitle(string title)
    {
        var value = title.Trim();
        if (value.Length == 0) throw new ArgumentException("请填写任务名称。");
        if (value.Length > 240) throw new ArgumentException("任务名称最多 240 个字符。");
        return value;
    }
}

public sealed record DaySummary(int Pending, int Completed)
{
    public int Total => Pending + Completed;
    public double Percent => Total == 0 ? 0 : 100d * Completed / Total;
}

public static class TodoQueries
{
    public static bool CompletedOn(TodoItem item, DateOnly day, TimeZoneInfo zone)
        => !item.IsDeleted && item.CompletedAt is { } time &&
           DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(time, zone).DateTime) == day;

    public static DaySummary Summary(IEnumerable<TodoItem> items, DateOnly today, TimeZoneInfo zone)
    {
        var active = items.Where(t => !t.IsDeleted).ToArray();
        return new(active.Count(t => !t.IsCompleted && t.PlannedDate <= today),
            active.Count(t => CompletedOn(t, today, zone)));
    }
}

public sealed record WindowPreferences(
    int? PixelX = null, int? PixelY = null, bool Collapsed = false,
    bool AlwaysOnTop = true, bool HideInFullscreen = true,
    int? CollapsedPixelX = null, int? CollapsedPixelY = null);
