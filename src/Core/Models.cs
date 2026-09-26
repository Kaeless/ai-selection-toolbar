using System;
using System.Collections.Generic;
using System.Runtime.Serialization;

namespace AiSelectionToolbar.Core
{
    [DataContract]
    public sealed class AppSettings
    {
        [DataMember(Order = 1)] public string BaseUrl { get; set; }
        [DataMember(Order = 2)] public string Model { get; set; }
        [DataMember(Order = 3)] public int TimeoutSeconds { get; set; } = 120;
        // Ciphertext only. Never place a plain API key in this serialized object.
        [DataMember(Order = 4)] public string ProtectedApiKey { get; set; }
        [DataMember(Order = 5)] public string TranslationTargetLanguage { get; set; } = "中文";
        [DataMember(Order = 6)] public List<string> ExcludedApplications { get; set; } = new List<string>();
        [DataMember(Order = 7)] public bool AutoShow { get; set; } = true;
        [DataMember(Order = 8)] public string NotesDirectory { get; set; }
        [DataMember(Order = 9)] public bool StartOnLogin { get; set; }
        [DataMember(Order = 10)] public List<CustomActionDefinition> CustomActions { get; set; } = new List<CustomActionDefinition>();
        [DataMember(Order = 11)] public string ToolbarStyle { get; set; } = "standard";
        [DataMember(Order = 12)] public string ToolbarAccentColor { get; set; } = "#4F46E5";
        [DataMember(Order = 13)] public List<ApiProfile> ApiProfiles { get; set; } = new List<ApiProfile>();
        [DataMember(Order = 14)] public string ActiveApiId { get; set; }

        public ApiProfile ActiveApi
        {
            get
            {
                EnsureApiProfiles();
                return ApiProfiles.Find(profile => profile != null && profile.Id == ActiveApiId);
            }
        }

        // Local OpenAI-compatible endpoints may intentionally have no API key.
        public bool IsConfigured
        {
            get
            {
                var profile = ActiveApi;
                return profile != null && !string.IsNullOrWhiteSpace(profile.BaseUrl) &&
                    !string.IsNullOrWhiteSpace(profile.Model);
            }
        }

        public void EnsureApiProfiles()
        {
            if (ApiProfiles == null) ApiProfiles = new List<ApiProfile>();
            if (ApiProfiles.Count == 0 && (!string.IsNullOrWhiteSpace(BaseUrl) ||
                !string.IsNullOrWhiteSpace(Model) || !string.IsNullOrWhiteSpace(ProtectedApiKey)))
            {
                ApiProfiles.Add(new ApiProfile {
                    Id = "legacy", Name = "默认 API", BaseUrl = BaseUrl,
                    Model = Model, ProtectedApiKey = ProtectedApiKey
                });
            }
            ApiProfiles.RemoveAll(profile => profile == null);
            if (ApiProfiles.Count == 0)
            {
                BaseUrl = null;
                Model = null;
                ProtectedApiKey = null;
                ActiveApiId = null;
                return;
            }
            if (string.IsNullOrWhiteSpace(ActiveApiId) ||
                !ApiProfiles.Exists(profile => profile.Id == ActiveApiId))
                ActiveApiId = ApiProfiles[0].Id;
            var active = ApiProfiles.Find(profile => profile.Id == ActiveApiId);
            BaseUrl = active.BaseUrl;
            Model = active.Model;
            ProtectedApiKey = active.ProtectedApiKey;
        }

        [OnDeserializing]
        private void InitializeDefaults(StreamingContext context)
        {
            TimeoutSeconds = 120;
            TranslationTargetLanguage = "中文";
            ExcludedApplications = new List<string>();
            AutoShow = true;
            CustomActions = new List<CustomActionDefinition>();
            ToolbarStyle = "standard";
            ToolbarAccentColor = "#4F46E5";
            ApiProfiles = new List<ApiProfile>();
        }
    }

    [DataContract]
    public sealed class ApiProfile
    {
        [DataMember(Order = 1)] public string Id { get; set; }
        [DataMember(Order = 2)] public string Name { get; set; }
        [DataMember(Order = 3)] public string BaseUrl { get; set; }
        [DataMember(Order = 4)] public string Model { get; set; }
        [DataMember(Order = 5)] public string ProtectedApiKey { get; set; }
    }

    [DataContract]
    public sealed class CustomActionDefinition
    {
        [DataMember(Order = 1)] public string Id { get; set; }
        [DataMember(Order = 2)] public string Name { get; set; }
        [DataMember(Order = 3)] public string Prompt { get; set; }
    }

    public sealed class ChatMessage
    {
        public string Role { get; set; }
        public string Content { get; set; }

        public ChatMessage(string role, string content)
        {
            Role = role ?? throw new ArgumentNullException(nameof(role));
            Content = content ?? throw new ArgumentNullException(nameof(content));
        }
    }

    public sealed class ChatDelta
    {
        public string Text { get; set; }
        public string FinishReason { get; set; }
        public bool IsReasoning { get; set; }
    }

    public sealed class HistoryEntry
    {
        public long Id { get; set; }
        public DateTime CreatedUtc { get; set; }
        public string Source { get; set; }
        public string SelectedText { get; set; }
        public string Response { get; set; }
        public string Action { get; set; }
        public string Prompt { get; set; }
        public string SourceApplication { get; set; }
        public string SourceFile { get; set; }
        public string SourceTitle { get; set; }
    }

    public sealed class DuplicateInfo
    {
        public int Count { get; set; }
        public DateTime? LastSeenUtc { get; set; }
        public bool IsDuplicate => Count > 0;
    }
}
