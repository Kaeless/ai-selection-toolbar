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
                    var settings = (AppSettings)new DataContractJsonSerializer(typeof(AppSettings)).ReadObject(file);
                    settings.EnsureApiProfiles();
                    return settings;
                }
            }
        }

        public string ReadApiKey(AppSettings settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            var profile = settings.ActiveApi;
            if (profile == null || string.IsNullOrEmpty(profile.ProtectedApiKey)) return null;
            var encrypted = Convert.FromBase64String(profile.ProtectedApiKey);
            var plain = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }

        public void SetApiKey(AppSettings settings, string apiKey)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            SetApiKey(settings.ActiveApi, apiKey);
            settings.EnsureApiProfiles();
        }

        public void SetApiKey(ApiProfile profile, string apiKey)
        {
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                if (profile != null) profile.ProtectedApiKey = null;
                return;
            }
            if (profile == null) throw new InvalidOperationException("请先创建 API 配置。");
            var plain = Encoding.UTF8.GetBytes(apiKey);
            try
            {
                var protectedValue = Convert.ToBase64String(
                    ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser));
                profile.ProtectedApiKey = protectedValue;
            }
            finally { Array.Clear(plain, 0, plain.Length); }
        }

        public void Save(AppSettings settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            settings.EnsureApiProfiles();
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
