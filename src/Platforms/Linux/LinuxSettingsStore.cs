using System.Runtime.Serialization.Json;
using System.Text;
using AiSelectionToolbar.Core;

namespace AiSelectionToolbar.Linux;

// Keep the secret outside settings.json. Both files are private to the current Unix user.
internal sealed class LinuxSettingsStore
{
    private readonly string directory;
    private string SettingsPath => Path.Combine(directory, "settings.json");
    private string SecretPath => Path.Combine(directory, "api-key");

    public LinuxSettingsStore(string directory) => this.directory = directory;

    public AppSettings Load()
    {
        if (!File.Exists(SettingsPath)) return new AppSettings();
        using var input = File.OpenRead(SettingsPath);
        var result = (AppSettings)new DataContractJsonSerializer(typeof(AppSettings)).ReadObject(input);
        // DPAPI ciphertext written on Windows is not readable here and must never be sent as a key.
        result.ProtectedApiKey = null;
        return result;
    }

    public string ReadApiKey() => File.Exists(SecretPath) ? File.ReadAllText(SecretPath, Encoding.UTF8) : null;

    public void Save(AppSettings settings, string newKey, bool endpointChanged)
    {
        Directory.CreateDirectory(directory);
        File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        settings.ProtectedApiKey = null;
        var tmp = SettingsPath + "." + Guid.NewGuid().ToString("N");
        try
        {
            using (var output = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                new DataContractJsonSerializer(typeof(AppSettings)).WriteObject(output, settings);
                output.Flush(true);
            }
            File.Move(tmp, SettingsPath, true);
        }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }

        if (!string.IsNullOrEmpty(newKey))
        {
            var secretTmp = SecretPath + "." + Guid.NewGuid().ToString("N");
            try
            {
                using (var output = new FileStream(secretTmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    File.SetUnixFileMode(secretTmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                    var bytes = Encoding.UTF8.GetBytes(newKey);
                    output.Write(bytes);
                    output.Flush(true);
                    Array.Clear(bytes);
                }
                File.Move(secretTmp, SecretPath, true);
            }
            finally { if (File.Exists(secretTmp)) File.Delete(secretTmp); }
        }
        else if (endpointChanged && File.Exists(SecretPath)) File.Delete(SecretPath);
    }
}
