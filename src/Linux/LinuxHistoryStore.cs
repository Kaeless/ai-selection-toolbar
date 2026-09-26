using AiSelectionToolbar.Core;
using Microsoft.Data.Sqlite;
using System.Text.RegularExpressions;

namespace AiSelectionToolbar.Linux;

// Uses the Windows history schema so existing data can be imported without conversion.
internal sealed class LinuxHistoryStore : IDisposable
{
    private readonly SqliteConnection db;
    private readonly object gate = new();

    public LinuxHistoryStore(string path)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        db.Open();
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        using var command = db.CreateCommand();
        command.CommandText = "CREATE TABLE IF NOT EXISTS history (id INTEGER PRIMARY KEY AUTOINCREMENT, " +
            "created_utc_ticks INTEGER NOT NULL, selection_hash TEXT NOT NULL, selected_text TEXT NOT NULL, " +
            "action TEXT, prompt TEXT, source TEXT, source_application TEXT, source_file TEXT, source_title TEXT, response TEXT); " +
            "CREATE INDEX IF NOT EXISTS ix_history_duplicate ON history(selection_hash, created_utc_ticks)";
        command.ExecuteNonQuery();
    }

    public void Add(HistoryEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.SelectedText)) return;
        lock (gate)
        {
            using var command = db.CreateCommand();
            command.CommandText = "INSERT INTO history(created_utc_ticks,selection_hash,selected_text,action,prompt,source,source_application,source_file,source_title,response) " +
                "VALUES($time,$hash,$text,$action,$prompt,$source,$app,$file,$title,$response)";
            var normalized = Regex.Replace(entry.SelectedText.Trim(), @"\s+", " ");
            var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(normalized));
            command.Parameters.AddWithValue("$time", (entry.CreatedUtc == default ? DateTime.UtcNow : entry.CreatedUtc.ToUniversalTime()).Ticks);
            command.Parameters.AddWithValue("$hash", Convert.ToBase64String(bytes));
            command.Parameters.AddWithValue("$text", entry.SelectedText);
            command.Parameters.AddWithValue("$action", (object)entry.Action ?? DBNull.Value);
            command.Parameters.AddWithValue("$prompt", (object)entry.Prompt ?? DBNull.Value);
            command.Parameters.AddWithValue("$source", (object)entry.Source ?? DBNull.Value);
            command.Parameters.AddWithValue("$app", (object)entry.SourceApplication ?? DBNull.Value);
            command.Parameters.AddWithValue("$file", (object)entry.SourceFile ?? DBNull.Value);
            command.Parameters.AddWithValue("$title", (object)entry.SourceTitle ?? DBNull.Value);
            command.Parameters.AddWithValue("$response", (object)entry.Response ?? DBNull.Value);
            command.ExecuteNonQuery();
        }
    }

    public IList<HistoryEntry> Search(string term)
    {
        lock (gate)
        {
            using var command = db.CreateCommand();
            command.CommandText = "SELECT id,created_utc_ticks,selected_text,action,prompt,source,source_application,source_file,source_title,response " +
                "FROM history WHERE $term = '' OR instr(lower(selected_text),lower($term)) > 0 OR " +
                "instr(lower(coalesce(response,'')),lower($term)) > 0 OR instr(lower(coalesce(source,'')),lower($term)) > 0 " +
                "ORDER BY id DESC LIMIT 100";
            command.Parameters.AddWithValue("$term", term ?? "");
            using var reader = command.ExecuteReader();
            var items = new List<HistoryEntry>();
            string Value(int i) => reader.IsDBNull(i) ? null : reader.GetString(i);
            while (reader.Read()) items.Add(new HistoryEntry {
                Id = reader.GetInt64(0), CreatedUtc = new DateTime(reader.GetInt64(1), DateTimeKind.Utc),
                SelectedText = reader.GetString(2), Action = Value(3), Prompt = Value(4),
                Source = Value(5), SourceApplication = Value(6), SourceFile = Value(7),
                SourceTitle = Value(8), Response = Value(9)
            });
            return items;
        }
    }

    public bool HasNote(string selectedText, string response)
    {
        lock (gate)
        {
            using var command = db.CreateCommand();
            command.CommandText = "SELECT 1 FROM history WHERE action = 'note' AND selected_text = $text AND response = $response LIMIT 1";
            command.Parameters.AddWithValue("$text", selectedText);
            command.Parameters.AddWithValue("$response", response);
            return command.ExecuteScalar() != null;
        }
    }

    public void Dispose() => db.Dispose();
}
