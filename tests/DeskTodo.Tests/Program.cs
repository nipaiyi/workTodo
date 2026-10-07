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

string LegacySnapshot(string path)
{
    using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
    connection.Open();
    using var command = connection.CreateCommand();
    command.CommandText = "PRAGMA user_version;";
    var version = Convert.ToInt32(command.ExecuteScalar());
    List<string?[]> ReadRows(string sql)
    {
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var rows = new List<string?[]>();
        while (reader.Read()) rows.Add(Enumerable.Range(0, reader.FieldCount)
            .Select(i => reader.IsDBNull(i) ? null : reader.GetString(i)).ToArray());
        return rows;
    }
    return System.Text.Json.JsonSerializer.Serialize(new { version,
        tasks = ReadRows("SELECT id,title,notes,planned_date,created_at,updated_at,completed_at,deleted_at FROM tasks ORDER BY id;"),
        settings = ReadRows("SELECT key,value FROM settings ORDER BY key;") });
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
    Check(today.Priority == TodoPriority.Normal && today.PriorityLabel == "抓点紧", "default priority is normal");
    Reject(() => TodoItem.Create("Invalid", "", day, now, (TodoPriority)3), "invalid priority rejected on creation");
    Reject(() => store.Save(today with { Priority = (TodoPriority)(-1) }), "invalid priority rejected before saving");
    var old = TodoItem.Create("Yesterday", "", day.AddDays(-1), now, TodoPriority.Urgent);
    var future = TodoItem.Create("Tomorrow", "", day.AddDays(1), now, TodoPriority.Relaxed);
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
    Check(reopened.ReadAll().Single(t => t.Id == old.Id).Priority == TodoPriority.Urgent &&
          reopened.ReadAll().Single(t => t.Id == future.Id).Priority == TodoPriority.Relaxed, "all priority levels survive restart");
    store.Save(today with { Priority = TodoPriority.Relaxed });
    Check(store.ReadAll().Single(t => t.Id == today.Id).Priority == TodoPriority.Relaxed, "editing priority persists without duplicating tasks");
    store.Save(today);
    var summary = TodoQueries.Summary(store.ReadAll(), day, zone);
    Check(summary.Pending == 2 && summary.Completed == 0, "today includes overdue but not future");
    var completedOld = old with { CompletedAt = now, UpdatedAt = now };
    store.Save(completedOld);
    Check(store.ReadAll().Single(t => t.Id == old.Id).PlannedDate == day.AddDays(-1), "complete old task keeps plan date");
    Check(TodoQueries.CompletedOn(completedOld, day, zone), "old task counted on actual completion day");
    Check(store.ReadAll().Single(t => t.Id == old.Id).Priority == TodoPriority.Urgent, "completion preserves priority");
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
    var anchoredSettings = settings with { CollapsedPixelX = -1800, CollapsedPixelY = 900 };
    store.SavePreferences(anchoredSettings);
    Check(new TodoStore(store.DatabasePath).ReadPreferences() == anchoredSettings, "widget position persists independently from expanded position");
    using (var connection = new SqliteConnection($"Data Source={store.DatabasePath};Pooling=False"))
    {
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE settings SET value=$value WHERE key='window';";
        command.Parameters.AddWithValue("$value", System.Text.Json.JsonSerializer.Serialize(new
            { settings.PixelX, settings.PixelY, settings.Collapsed, settings.AlwaysOnTop, settings.HideInFullscreen }));
        command.ExecuteNonQuery();
    }
    Check(store.ReadPreferences() == settings, "legacy window settings remain compatible without widget coordinates");
    store.SavePreferences(settings);
    var backup = Path.Combine(directory, "export.sqlite");
    store.Backup(backup);
    Check(new TodoStore(backup).ReadAll().Count == 3, "consistent SQLite backup readable");
    Reject(() => store.Backup(store.DatabasePath), "live database cannot be backup destination");
    store.Save(TodoItem.Create("Added later", "", day, now));
    Check(store.Restore(backup) == 3 && store.ReadAll().Count == 3, "restore replaces tasks atomically");
    Check(store.ReadAll().Single(t => t.Id == future.Id).Priority == TodoPriority.Relaxed, "backup restore preserves priority");
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

    var legacyDirectory = Path.Combine(directory, "legacy");
    Directory.CreateDirectory(legacyDirectory);
    var legacyPath = Path.Combine(legacyDirectory, "tasks.sqlite");
    using (var connection = new SqliteConnection($"Data Source={legacyPath};Pooling=False"))
    {
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE tasks(id TEXT PRIMARY KEY, title TEXT NOT NULL, notes TEXT NOT NULL,
                planned_date TEXT NOT NULL, created_at TEXT NOT NULL, updated_at TEXT NOT NULL, completed_at TEXT, deleted_at TEXT);
            CREATE INDEX tasks_planned_date ON tasks(planned_date);
            CREATE TABLE settings(key TEXT PRIMARY KEY, value TEXT NOT NULL);
            INSERT INTO settings VALUES('application','DeskTodo'),('window',$settings);
            PRAGMA user_version=1;
            """;
        command.Parameters.AddWithValue("$settings", System.Text.Json.JsonSerializer.Serialize(settings));
        command.ExecuteNonQuery();
        foreach (var item in new[] { today, completedOld, future with { DeletedAt = now } })
        {
            command.Parameters.Clear();
            command.CommandText = "INSERT INTO tasks VALUES($id,$title,$notes,$date,$created,$updated,$completed,$deleted);";
            command.Parameters.AddWithValue("$id", item.Id.ToString());
            command.Parameters.AddWithValue("$title", item.Title);
            command.Parameters.AddWithValue("$notes", item.Notes);
            command.Parameters.AddWithValue("$date", item.PlannedDate.ToString("yyyy-MM-dd"));
            command.Parameters.AddWithValue("$created", item.CreatedAt.ToString("O"));
            command.Parameters.AddWithValue("$updated", item.UpdatedAt.ToString("O"));
            command.Parameters.AddWithValue("$completed", (object?)item.CompletedAt?.ToString("O") ?? DBNull.Value);
            command.Parameters.AddWithValue("$deleted", (object?)item.DeletedAt?.ToString("O") ?? DBNull.Value);
            command.ExecuteNonQuery();
        }
    }
    var legacyCopy = Path.Combine(directory, "legacy-export.sqlite");
    File.Copy(legacyPath, legacyCopy);
    var originalBytes = File.ReadAllBytes(legacyCopy);
    var migrated = new TodoStore(legacyPath);
    Check(migrated.ReadAll().ToHashSet().SetEquals(new[] { today, completedOld with { Priority = TodoPriority.Normal },
        future with { DeletedAt = now, Priority = TodoPriority.Normal } }), "v1 migration preserves every task field and adds normal priority");
    Check(migrated.ReadPreferences() == settings, "v1 migration preserves window preferences");
    var migrationBackup = Directory.GetFiles(Path.Combine(legacyDirectory, "backups"), "before-priority-upgrade-*.sqlite").Single();
    Check(LegacySnapshot(migrationBackup) == LegacySnapshot(legacyCopy), "migration backup preserves v1 schema, all fields and settings");
    migrated.Save(migrated.ReadAll().Single(t => t.Id == today.Id) with { Priority = TodoPriority.Urgent });
    Check(new TodoStore(legacyPath).ReadAll().Single(t => t.Id == today.Id).Priority == TodoPriority.Urgent,
        "repeated startup does not reset priorities");
    Check(Directory.GetFiles(Path.Combine(legacyDirectory, "backups"), "before-priority-upgrade-*.sqlite").Length == 1,
        "migration runs only once");
    migrated.DailyBackup(day.AddYears(1));
    Check(File.Exists(migrationBackup), "daily retention keeps migration recovery snapshot");
    Check(store.Restore(legacyCopy) == 3 && store.ReadAll().All(t => t.Priority == TodoPriority.Normal), "v1 backup remains restorable with default priorities");
    Check(File.ReadAllBytes(legacyCopy).SequenceEqual(originalBytes), "restoring v1 backup does not mutate its source");

    var blockedDirectory = Path.Combine(directory, "blocked-upgrade");
    Directory.CreateDirectory(blockedDirectory);
    var blockedPath = Path.Combine(blockedDirectory, "tasks.sqlite");
    File.Copy(legacyCopy, blockedPath);
    File.WriteAllText(Path.Combine(blockedDirectory, "backups"), "backup directory blocked");
    Reject(() => new TodoStore(blockedPath), "failed migration backup aborts upgrade");
    Check(File.ReadAllBytes(blockedPath).SequenceEqual(originalBytes), "failed migration leaves original database untouched");

    var newerPath = Path.Combine(directory, "newer.sqlite");
    File.Copy(store.DatabasePath, newerPath);
    using (var connection = new SqliteConnection($"Data Source={newerPath};Pooling=False"))
    {
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version=99;";
        command.ExecuteNonQuery();
    }
    var newerBytes = File.ReadAllBytes(newerPath);
    Reject(() => new TodoStore(newerPath), "newer database cannot be downgraded");
    Reject(() => store.Restore(newerPath), "newer backup cannot be imported");
    Check(File.ReadAllBytes(newerPath).SequenceEqual(newerBytes) && store.ReadAll().Count == 3, "version rejection preserves both databases");
    Console.WriteLine($"{checks} checks passed. Data: {directory}");
    return 0;
}
catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
