using System;
using System.IO;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;

namespace AiSelectionToolbar.Core
{
    /// <summary>Persists ordinary preferences and a DPAPI CurrentUser encrypted API key.</summary>
    public sealed class SettingsStore
    {
        private readonly string path;
        private readonly object gate = new object();

        public SettingsStore(string path)
        {
            this.path = path ?? throw new ArgumentNullException(nameof(path));
        }

        public static string DefaultPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AiSelectionToolbar", "settings.json");

        public AppSettings Load()
        {
            lock (gate)
            {
                if (!File.Exists(path)) return new AppSettings();
                using (var file = File.OpenRead(path))
                {
                    return (AppSettings)new DataContractJsonSerializer(typeof(AppSettings)).ReadObject(file);
                }
            }
        }

        public string ReadApiKey(AppSettings settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            if (string.IsNullOrEmpty(settings.ProtectedApiKey)) return null;
            var encrypted = Convert.FromBase64String(settings.ProtectedApiKey);
            var plain = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }

        public void SetApiKey(AppSettings settings, string apiKey)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                settings.ProtectedApiKey = null;
                return;
            }
            var plain = Encoding.UTF8.GetBytes(apiKey);
            try
            {
                settings.ProtectedApiKey = Convert.ToBase64String(
                    ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser));
            }
            finally { Array.Clear(plain, 0, plain.Length); }
        }

        public void Save(AppSettings settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            lock (gate)
            {
                var folder = Path.GetDirectoryName(Path.GetFullPath(path));
                Directory.CreateDirectory(folder);
                var temporary = path + ".tmp." + Guid.NewGuid().ToString("N");
                try
                {
                    using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        new DataContractJsonSerializer(typeof(AppSettings)).WriteObject(file, settings);
                        file.Flush(true);
                    }
                    if (File.Exists(path)) File.Replace(temporary, path, null);
                    else File.Move(temporary, path);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
        }
    }
}
