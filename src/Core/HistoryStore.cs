using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace AiSelectionToolbar.Core
{
    /// <summary>SQLite history with the application-bundled provider, including Windows 7 SP1.</summary>
    public sealed class HistoryStore : IDisposable
    {
        private readonly object gate = new object();
        private readonly SQLiteConnection connection;
        private bool disposed;

        public static string DefaultPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AiSelectionToolbar", "history.db");

        public HistoryStore(string path)
        {
            if (path == null) throw new ArgumentNullException(nameof(path));
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            connection = new SQLiteConnection(new SQLiteConnectionStringBuilder {
                DataSource = Path.GetFullPath(path), Version = 3, BusyTimeout = 5000
            }.ConnectionString);
            connection.Open();
            try
            {
                Execute("CREATE TABLE IF NOT EXISTS history (" +
                    "id INTEGER PRIMARY KEY AUTOINCREMENT, created_utc_ticks INTEGER NOT NULL, " +
                    "selection_hash TEXT NOT NULL, selected_text TEXT NOT NULL, action TEXT, " +
                    "prompt TEXT, source TEXT, source_application TEXT, source_file TEXT, " +
                    "source_title TEXT, response TEXT)");
                Execute("CREATE INDEX IF NOT EXISTS ix_history_duplicate ON history(selection_hash, created_utc_ticks)");
            }
            catch { Dispose(); throw; }
        }

        public long Add(HistoryEntry entry)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));
            if (string.IsNullOrWhiteSpace(entry.SelectedText)) throw new ArgumentException("Selected text is required.", nameof(entry));
            lock (gate)
            {
                EnsureOpen();
                var time = entry.CreatedUtc == default(DateTime) ? DateTime.UtcNow : entry.CreatedUtc.ToUniversalTime();
                using (var cmd = Command("INSERT INTO history " +
                    "(created_utc_ticks, selection_hash, selected_text, action, prompt, source, source_application, source_file, source_title, response) " +
                    "VALUES (@time, @hash, @text, @action, @prompt, @source, @app, @file, @title, @response)"))
                {
                    AddParameter(cmd, "@time", time.Ticks);
                    AddParameter(cmd, "@hash", Fingerprint(entry.SelectedText));
                    AddParameter(cmd, "@text", entry.SelectedText);
                    AddParameter(cmd, "@action", entry.Action);
                    AddParameter(cmd, "@prompt", entry.Prompt);
                    AddParameter(cmd, "@source", entry.Source);
                    AddParameter(cmd, "@app", entry.SourceApplication);
                    AddParameter(cmd, "@file", entry.SourceFile);
                    AddParameter(cmd, "@title", entry.SourceTitle);
                    AddParameter(cmd, "@response", entry.Response);
                    cmd.ExecuteNonQuery();
                }
                entry.Id = connection.LastInsertRowId;
                entry.CreatedUtc = time;
                return entry.Id;
            }
        }

        /// <summary>Call before Add to show a reminder about earlier matching selections.</summary>
        public DuplicateInfo FindDuplicate(string selectedText, TimeSpan lookback)
        {
            if (selectedText == null) throw new ArgumentNullException(nameof(selectedText));
            if (lookback < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(lookback));
            lock (gate)
            {
                EnsureOpen();
                using (var cmd = Command("SELECT COUNT(*), MAX(created_utc_ticks) FROM history " +
                    "WHERE selection_hash = @hash AND created_utc_ticks >= @since"))
                {
                    AddParameter(cmd, "@hash", Fingerprint(selectedText));
                    AddParameter(cmd, "@since", DateTime.UtcNow.Subtract(lookback).Ticks);
                    using (var reader = cmd.ExecuteReader())
                    {
                        reader.Read();
                        var count = checked((int)reader.GetInt64(0));
                        return new DuplicateInfo { Count = count,
                            LastSeenUtc = count == 0 ? (DateTime?)null :
                                new DateTime(reader.GetInt64(1), DateTimeKind.Utc) };
                    }
                }
            }
        }

        public IList<HistoryEntry> Recent(int limit = 50) => Search(null, limit);

        public IList<HistoryEntry> Search(string term, int limit = 50, int offset = 0)
        {
            if (limit < 1 || limit > 1000) throw new ArgumentOutOfRangeException(nameof(limit));
            if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
            lock (gate)
            {
                EnsureOpen();
                var entries = new List<HistoryEntry>();
                // instr treats the search text literally; SQL LIKE wildcards cannot broaden a search.
                using (var cmd = Command("SELECT id, created_utc_ticks, selected_text, action, prompt, " +
                    "source, source_application, source_file, source_title, response FROM history " +
                    "WHERE @term IS NULL OR instr(lower(selected_text), lower(@term)) > 0 " +
                    "OR instr(lower(coalesce(prompt, '')), lower(@term)) > 0 " +
                    "OR instr(lower(coalesce(response, '')), lower(@term)) > 0 " +
                    "OR instr(lower(coalesce(source, '')), lower(@term)) > 0 " +
                    "OR instr(lower(coalesce(source_application, '')), lower(@term)) > 0 " +
                    "OR instr(lower(coalesce(source_file, '')), lower(@term)) > 0 " +
                    "OR instr(lower(coalesce(source_title, '')), lower(@term)) > 0 " +
                    "ORDER BY id DESC LIMIT @limit OFFSET @offset"))
                {
                    AddParameter(cmd, "@term", string.IsNullOrWhiteSpace(term) ? null : term.Trim());
                    AddParameter(cmd, "@limit", limit);
                    AddParameter(cmd, "@offset", offset);
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                            entries.Add(new HistoryEntry {
                                Id = reader.GetInt64(0),
                                CreatedUtc = new DateTime(reader.GetInt64(1), DateTimeKind.Utc),
                                SelectedText = reader.GetString(2), Action = ReadString(reader, 3),
                                Prompt = ReadString(reader, 4), Source = ReadString(reader, 5),
                                SourceApplication = ReadString(reader, 6), SourceFile = ReadString(reader, 7),
                                SourceTitle = ReadString(reader, 8), Response = ReadString(reader, 9)
                            });
                    }
                }
                return entries;
            }
        }

        public bool HasNote(string selectedText, string response)
        {
            lock (gate)
            {
                EnsureOpen();
                using (var cmd = Command("SELECT 1 FROM history WHERE action = 'note' " +
                    "AND selected_text = @text AND response = @response LIMIT 1"))
                {
                    AddParameter(cmd, "@text", selectedText);
                    AddParameter(cmd, "@response", response);
                    return cmd.ExecuteScalar() != null;
                }
            }
        }

        public bool Delete(long id)
        {
            lock (gate)
            {
                EnsureOpen();
                using (var cmd = Command("DELETE FROM history WHERE id = @id"))
                {
                    AddParameter(cmd, "@id", id);
                    return cmd.ExecuteNonQuery() != 0;
                }
            }
        }

        public int Clear()
        {
            lock (gate)
            {
                EnsureOpen();
                using (var cmd = Command("DELETE FROM history")) return cmd.ExecuteNonQuery();
            }
        }

        private static string Fingerprint(string value)
        {
            var normalized = Regex.Replace(value.Trim(), @"\s+", " ");
            using (var sha = SHA256.Create())
                return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(normalized)));
        }

        private static string ReadString(SQLiteDataReader reader, int ordinal) =>
            reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

        private static void AddParameter(SQLiteCommand command, string name, object value) =>
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);

        private SQLiteCommand Command(string sql) => new SQLiteCommand(sql, connection);

        private void Execute(string sql)
        {
            using (var command = Command(sql)) command.ExecuteNonQuery();
        }

        private void EnsureOpen() { if (disposed) throw new ObjectDisposedException(nameof(HistoryStore)); }

        public void Dispose()
        {
            lock (gate)
            {
                if (disposed) return;
                disposed = true;
                connection.Dispose();
            }
        }
    }
}
