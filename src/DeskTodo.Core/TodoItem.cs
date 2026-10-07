using System.Globalization;

namespace DeskTodo.Core;

public sealed record TodoItem(
    Guid Id, string Title, string Notes, DateOnly PlannedDate,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    DateTimeOffset? CompletedAt = null, DateTimeOffset? DeletedAt = null)
{
    public bool IsCompleted => CompletedAt.HasValue;
    public bool IsDeleted => DeletedAt.HasValue;
    public string DateLabel => PlannedDate.ToString("M月d日", CultureInfo.GetCultureInfo("zh-CN"));
    public string AccessibleTitle => (IsCompleted ? "已完成：" : "未完成：") + Title;

    public static TodoItem Create(string title, string notes, DateOnly date, DateTimeOffset now)
        => new(Guid.NewGuid(), ValidateTitle(title), notes.Trim(), date, now, now);

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
    bool AlwaysOnTop = true, bool HideInFullscreen = true);
