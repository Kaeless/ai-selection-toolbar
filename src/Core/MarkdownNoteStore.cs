using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Security.Cryptography;

namespace AiSelectionToolbar.Core
{
    /// <summary>Append notes to yyyy-MM-dd_来源_学习.md using local date and source.</summary>
    public sealed class MarkdownNoteStore
    {
        private readonly string directory;
        private readonly object gate = new object();

        public MarkdownNoteStore(string directory)
        {
            this.directory = directory ?? throw new ArgumentNullException(nameof(directory));
        }

        public static string DefaultDirectory => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "AiSelectionToolbar", "Notes");

        public string Append(HistoryEntry entry)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));
            if (string.IsNullOrWhiteSpace(entry.SelectedText))
                throw new ArgumentException("Selected text is required.", nameof(entry));

            var timestamp = entry.CreatedUtc == default(DateTime)
                ? DateTime.Now : entry.CreatedUtc.ToLocalTime();
            var identity = !string.IsNullOrWhiteSpace(entry.SourceFile) ? entry.SourceFile
                : !string.IsNullOrWhiteSpace(entry.SourceTitle) ? entry.SourceTitle
                : !string.IsNullOrWhiteSpace(entry.SourceApplication) ? entry.SourceApplication : entry.Source;
            var source = !string.IsNullOrWhiteSpace(entry.SourceFile)
                ? Path.GetFileNameWithoutExtension(entry.SourceFile) : identity;
            var filename = timestamp.ToString("yyyy-MM-dd") + "_" + SafeName(source, identity) + "_学习.md";
            var path = Path.Combine(directory, filename);
            var heading = "## " + timestamp.ToString("HH:mm:ss") + " · " + SafeHeading(entry.Action ?? "摘录") + "\n\n";
            var details = "来源：" + (entry.Source ?? entry.SourceApplication ?? entry.SourceFile ?? "未知")
                .Replace("\r", " ").Replace("\n", " ") + "\n\n";
            var quote = entry.SelectedText.Replace("\r\n", "\n").Replace('\r', '\n');
            var fence = new string('`', Math.Max(3, LongestBacktickRun(quote) + 1));
            var content = heading + details + fence + "\n" + quote + "\n" + fence + "\n\n";
            if (!string.IsNullOrEmpty(entry.Prompt))
                content += "**提问：** " + entry.Prompt.Replace("\r", " ").Replace("\n", " ") + "\n\n";
            if (!string.IsNullOrEmpty(entry.Response)) content += entry.Response.TrimEnd() + "\n\n";

            lock (gate)
            {
                Directory.CreateDirectory(directory);
                using (var file = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
                {
                    var originalLength = file.Length;
                    try
                    {
                        using (var writer = new StreamWriter(file, new UTF8Encoding(false), 1024, true))
                        {
                            if (originalLength == 0) writer.Write("# " + timestamp.ToString("yyyy-MM-dd") + " · " + SafeHeading(source ?? "未知来源") + "\n\n");
                            writer.Write(content);
                            writer.Flush();
                        }
                    }
                    catch
                    {
                        // Keep a failed append from leaving a half-written note behind.
                        try { file.SetLength(originalLength); } catch { /* Preserve the write failure. */ }
                        throw;
                    }
                }
            }
            return path;
        }

        private static string SafeName(string source, string identity)
        {
            if (string.IsNullOrWhiteSpace(source)) return "unknown";
            var safe = Regex.Replace(source, "[\\\\/:*?\"<>|\\x00-\\x1f]", "_").Trim(' ', '.');
            if (safe.Length > 60) safe = safe.Substring(0, 60).TrimEnd(' ', '.');
            if (safe.Length == 0) return "unknown";
            if (Regex.IsMatch(safe, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$", RegexOptions.IgnoreCase))
                safe = "_" + safe;
            // A path or sanitized title can otherwise collide with another source.
            if (source != identity || safe != source)
            {
                using (var sha = SHA256.Create())
                {
                    var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(identity ?? source));
                    safe += "-" + BitConverter.ToString(hash, 0, 4).Replace("-", "").ToLowerInvariant();
                }
            }
            return safe;
        }

        private static string SafeHeading(string text) => text.Replace("\r", " ").Replace("\n", " ");
        private static int LongestBacktickRun(string text)
        {
            var longest = 0;
            var current = 0;
            foreach (var character in text)
            {
                current = character == '`' ? current + 1 : 0;
                if (current > longest) longest = current;
            }
            return longest;
        }
    }
}
