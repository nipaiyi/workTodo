using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace DeskTodo.Core;

public sealed class TodoStore
{
    public const int SchemaVersion = 1;
    public string DatabasePath { get; }
    public string DataDirectory => Path.GetDirectoryName(DatabasePath)!;

    public TodoStore(string databasePath)
    {
        DatabasePath = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(DataDirectory);
        using var connection = Open();
        using var version = connection.CreateCommand();
        version.CommandText = "PRAGMA user_version;";
        var existing = Convert.ToInt32(version.ExecuteScalar(), CultureInfo.InvariantCulture);
        if (existing > SchemaVersion)
            throw new InvalidDataException("此数据由更新版本创建，请使用新版桌面待办打开。");
        if (existing != 0) ValidateDatabase(connection);
        else
        {
            version.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%';";
            if (Convert.ToInt32(version.ExecuteScalar(), CultureInfo.InvariantCulture) > 0)
                throw new InvalidDataException("此文件不是桌面待办数据库，原文件未被修改。");
        }
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS tasks (
                id TEXT PRIMARY KEY, title TEXT NOT NULL, notes TEXT NOT NULL,
                planned_date TEXT NOT NULL, created_at TEXT NOT NULL, updated_at TEXT NOT NULL,
                completed_at TEXT, deleted_at TEXT
            );
            CREATE INDEX IF NOT EXISTS tasks_planned_date ON tasks(planned_date);
            CREATE TABLE IF NOT EXISTS settings (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            INSERT OR IGNORE INTO settings(key, value) VALUES ('application', 'DeskTodo');
            PRAGMA user_version = 1;
            """;
        command.ExecuteNonQuery();
        transaction.Commit();
        ValidateDatabase(connection);
    }

    private SqliteConnection Open(SqliteOpenMode mode = SqliteOpenMode.ReadWriteCreate, string? path = null)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path ?? DatabasePath, Mode = mode, Pooling = false, DefaultTimeout = 5
        }.ToString());
        connection.Open();
        return connection;
    }

    public IReadOnlyList<TodoItem> ReadAll()
    {
        using var connection = Open();
        return ReadItems(connection);
    }

    private static List<TodoItem> ReadItems(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, title, notes, planned_date, created_at, updated_at, completed_at, deleted_at
            FROM tasks ORDER BY created_at, id;
            """;
        using var reader = command.ExecuteReader();
        var items = new List<TodoItem>();
        while (reader.Read())
        {
            var item = new TodoItem(Guid.Parse(reader.GetString(0)),
                TodoItem.ValidateTitle(reader.GetString(1)), reader.GetString(2),
                DateOnly.ParseExact(reader.GetString(3), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                ParseTime(reader.GetString(4)), ParseTime(reader.GetString(5)),
                reader.IsDBNull(6) ? null : ParseTime(reader.GetString(6)),
                reader.IsDBNull(7) ? null : ParseTime(reader.GetString(7)));
            items.Add(item);
        }
        return items;
    }

    private static DateTimeOffset ParseTime(string value)
        => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    public void Save(TodoItem item)
    {
        TodoItem.ValidateTitle(item.Title);
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        Save(connection, transaction, item);
        transaction.Commit();
    }

    private static void Save(SqliteConnection connection, SqliteTransaction transaction, TodoItem item)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO tasks(id,title,notes,planned_date,created_at,updated_at,completed_at,deleted_at)
            VALUES($id,$title,$notes,$date,$created,$updated,$completed,$deleted)
            ON CONFLICT(id) DO UPDATE SET title=excluded.title, notes=excluded.notes,
              planned_date=excluded.planned_date, updated_at=excluded.updated_at,
              completed_at=excluded.completed_at, deleted_at=excluded.deleted_at;
            """;
        command.Parameters.AddWithValue("$id", item.Id.ToString());
        command.Parameters.AddWithValue("$title", item.Title.Trim());
        command.Parameters.AddWithValue("$notes", item.Notes);
        command.Parameters.AddWithValue("$date", item.PlannedDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$created", item.CreatedAt.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$updated", item.UpdatedAt.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$completed", (object?)item.CompletedAt?.ToUniversalTime().ToString("O") ?? DBNull.Value);
        command.Parameters.AddWithValue("$deleted", (object?)item.DeletedAt?.ToUniversalTime().ToString("O") ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    public WindowPreferences ReadPreferences()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM settings WHERE key='window';";
        if (command.ExecuteScalar() is not string json) return new();
        try { return JsonSerializer.Deserialize<WindowPreferences>(json) ?? new(); }
        catch (JsonException) { return new(); }
    }

    public void SavePreferences(WindowPreferences preferences)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO settings(key,value) VALUES('window',$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value;";
        command.Parameters.AddWithValue("$value", JsonSerializer.Serialize(preferences));
        command.ExecuteNonQuery();
    }

    public void Backup(string destination)
    {
        destination = Path.GetFullPath(destination);
        if (string.Equals(destination, DatabasePath, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("备份不能覆盖正在使用的数据库。");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var staging = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var source = Open())
            using (var target = Open(path: staging)) source.BackupDatabase(target);
            File.Move(staging, destination, overwrite: true);
        }
        finally { if (File.Exists(staging)) File.Delete(staging); }
    }

    public void DailyBackup(DateOnly day)
    {
        var directory = Path.Combine(DataDirectory, "backups");
        Backup(Path.Combine(directory, $"desk-todo-{day:yyyy-MM-dd}.sqlite"));
        var cutoff = day.AddDays(-13);
        foreach (var path in Directory.EnumerateFiles(directory, "desk-todo-????-??-??.sqlite"))
        {
            var name = Path.GetFileNameWithoutExtension(path)[10..];
            if (DateOnly.TryParseExact(name, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                && date < cutoff) File.Delete(path);
        }
    }

    public int Restore(string backup)
    {
        backup = Path.GetFullPath(backup);
        if (string.Equals(backup, DatabasePath, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("请选择导出的备份文件。");
        List<TodoItem> items;
        using (var source = Open(SqliteOpenMode.ReadOnly, backup))
        {
            ValidateDatabase(source);
            items = ReadItems(source);
        }
        Backup(Path.Combine(DataDirectory, "backups", $"before-restore-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.sqlite"));
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM tasks;";
        command.ExecuteNonQuery();
        foreach (var item in items) Save(connection, transaction, item);
        transaction.Commit();
        return items.Count;
    }

    private static void ValidateDatabase(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check;";
        if (!string.Equals(command.ExecuteScalar()?.ToString(), "ok", StringComparison.Ordinal))
            throw new InvalidDataException("数据库完整性检查失败，请保留原文件并尝试恢复备份。");
        command.CommandText = "PRAGMA user_version;";
        if (Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) != SchemaVersion)
            throw new InvalidDataException("备份版本不受支持。");
        command.CommandText = "SELECT value FROM settings WHERE key='application';";
        if (command.ExecuteScalar()?.ToString() != "DeskTodo")
            throw new InvalidDataException("这不是桌面待办的备份文件。");
        _ = ReadItems(connection);
    }
}
