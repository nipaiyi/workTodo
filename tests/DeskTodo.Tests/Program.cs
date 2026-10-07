using DeskTodo.Core;
using Microsoft.Data.Sqlite;

var directory = Path.GetFullPath(args.Length > 0 ? args[0] : "artifacts/core-tests");
directory = Path.Combine(directory, Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
var checks = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL: " + name);
    Console.WriteLine("PASS: " + name);
    checks++;
}
void Reject(Action action, string name)
{
    try { action(); } catch (Exception) { Check(true, name); return; }
    throw new Exception("FAIL: expected rejection: " + name);
}

try
{
    var day = new DateOnly(2026, 10, 7);
    var now = new DateTimeOffset(2026, 10, 7, 3, 0, 0, TimeSpan.Zero);
    var zone = TimeZoneInfo.FindSystemTimeZoneById("China Standard Time");
    var store = new TodoStore(Path.Combine(directory, "tasks.sqlite"));
    Check(store.ReadAll().Count == 0, "fresh database");
    Reject(() => TodoItem.Create("  ", "", day, now), "blank title rejected");
    Reject(() => TodoItem.Create(new string('x', 241), "", day, now), "overlong title rejected");
    var today = TodoItem.Create(" today's task ' $value ", "多行\n备注", day, now);
    var old = TodoItem.Create("Yesterday", "", day.AddDays(-1), now);
    var future = TodoItem.Create("Tomorrow", "", day.AddDays(1), now);
    store.Save(today); store.Save(old); store.Save(future);
    Check(store.ReadAll().Count == 3, "parameterized text and unicode persisted");
    store.Save(today);
    Check(store.ReadAll().Count == 3, "same task saved without duplication");
    using (var locked = new SqliteConnection($"Data Source={store.DatabasePath};Pooling=False"))
    {
        locked.Open();
        using var transaction = locked.BeginTransaction();
        Reject(() => store.Save(TodoItem.Create("Cannot save", "", day, now)), "locked database rejects writes");
    }
    Check(store.ReadAll().Count == 3, "failed write leaves tasks intact");
    var reopened = new TodoStore(store.DatabasePath);
    Check(reopened.ReadAll().Single(t => t.Id == today.Id).Notes == today.Notes, "restart preserves content");
    var summary = TodoQueries.Summary(store.ReadAll(), day, zone);
    Check(summary.Pending == 2 && summary.Completed == 0, "today includes overdue but not future");
    var completedOld = old with { CompletedAt = now, UpdatedAt = now };
    store.Save(completedOld);
    Check(store.ReadAll().Single(t => t.Id == old.Id).PlannedDate == day.AddDays(-1), "complete old task keeps plan date");
    Check(TodoQueries.CompletedOn(completedOld, day, zone), "old task counted on actual completion day");
    summary = TodoQueries.Summary(store.ReadAll(), day, zone);
    Check(summary.Pending == 1 && summary.Completed == 1 && summary.Percent == 50, "consistent progress");
    var midnight = today with { CompletedAt = new DateTimeOffset(2026, 10, 6, 16, 1, 0, TimeSpan.Zero) };
    Check(TodoQueries.CompletedOn(midnight, day, zone), "UTC completion converted to local day");
    Check(!TodoQueries.CompletedOn(midnight, day.AddDays(-1), zone), "UTC date not mistaken for local day");
    store.Save(old);
    Check(!store.ReadAll().Single(t => t.Id == old.Id).IsCompleted, "completion undone persistently");
    var deleted = today with { DeletedAt = now };
    store.Save(deleted);
    Check(TodoQueries.Summary(store.ReadAll(), day, zone).Pending == 1, "trash excluded from progress");
    store.Save(today);
    Check(!store.ReadAll().Single(t => t.Id == today.Id).IsDeleted, "trash restored persistently");
    var tomorrowSummary = TodoQueries.Summary(store.ReadAll(), day.AddDays(1), zone);
    Check(tomorrowSummary.Pending == 3 && store.ReadAll().Count == 3, "rollover carries overdue without copies");
    var settings = new WindowPreferences(-1900, 400, true, false, false);
    store.SavePreferences(settings);
    Check(new TodoStore(store.DatabasePath).ReadPreferences() == settings, "window preferences survive restart");
    var backup = Path.Combine(directory, "export.sqlite");
    store.Backup(backup);
    Check(new TodoStore(backup).ReadAll().Count == 3, "consistent SQLite backup readable");
    Reject(() => store.Backup(store.DatabasePath), "live database cannot be backup destination");
    store.Save(TodoItem.Create("Added later", "", day, now));
    Check(store.Restore(backup) == 3 && store.ReadAll().Count == 3, "restore replaces tasks atomically");
    Check(store.ReadPreferences() == settings, "restore preserves current window settings");
    Check(Directory.GetFiles(Path.Combine(directory, "backups"), "before-restore-*.sqlite").Length == 1, "restore makes recovery backup first");
    var unrelated = Path.Combine(directory, "unrelated.sqlite");
    using (var connection = new SqliteConnection($"Data Source={unrelated};Pooling=False"))
    {
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE unrelated(value TEXT);";
        command.ExecuteNonQuery();
    }
    Reject(() => store.Restore(unrelated), "foreign database import rejected");
    Check(store.ReadAll().Count == 3, "failed restore leaves current tasks intact");
    Reject(() => new TodoStore(unrelated), "foreign existing database not adopted");
    var corrupt = Path.Combine(directory, "corrupt.sqlite");
    File.WriteAllText(corrupt, "not a database");
    Reject(() => store.Restore(corrupt), "corrupt backup rejected");
    Check(store.ReadAll().Count == 3, "corrupt backup cannot erase current data");
    store.DailyBackup(day.AddDays(-14));
    store.DailyBackup(day);
    Check(!File.Exists(Path.Combine(directory, "backups", $"desk-todo-{day.AddDays(-14):yyyy-MM-dd}.sqlite")), "daily backup retention enforced");
    Check(File.Exists(Path.Combine(directory, "backups", $"desk-todo-{day:yyyy-MM-dd}.sqlite")), "daily backup generated");
    Console.WriteLine($"{checks} checks passed. Data: {directory}");
    return 0;
}
catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
