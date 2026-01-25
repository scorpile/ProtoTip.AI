using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace ProtoTipAI.Editor
{
    [Serializable]
    internal sealed class ProtoChatSession
    {
        public string id;
        public string title;
        public long createdAt;
        public long updatedAt;
        public string summary;
        public ProtoChatMessage[] messages;
        public string lastAgentGoal;
        public string[] agentHistory;
    }

    [Serializable]
    internal sealed class ProtoChatSessionInfo
    {
        public string id;
        public string title;
        public long createdAt;
        public long updatedAt;
    }

    [Serializable]
    internal sealed class ProtoChatSessionIndexSnapshot
    {
        public ProtoChatSessionInfo[] sessions;
    }

    internal static class ProtoChatSessionStore
    {
        private const string SessionFolder = "Assets/Plan/ChatSessions";
        private const string SessionIndexFile = "ChatSessions.json";

        public static string EnsureSessionFolder()
        {
            ProtoAssetUtility.TryEnsureFolder(SessionFolder, out var normalized, out _);
            return normalized;
        }

        public static string GetIndexPath()
        {
            EnsureSessionFolder();
            return $"{SessionFolder}/{SessionIndexFile}";
        }

        public static string GetSessionPath(string sessionId)
        {
            EnsureSessionFolder();
            var safeId = string.IsNullOrWhiteSpace(sessionId) ? Guid.NewGuid().ToString("N") : sessionId.Trim();
            return $"{SessionFolder}/{safeId}.json";
        }

        public static List<ProtoChatSessionInfo> LoadSessionIndex()
        {
            var path = Path.GetFullPath(GetIndexPath());
            if (!File.Exists(path))
            {
                return new List<ProtoChatSessionInfo>();
            }

            try
            {
                var json = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(json))
                {
                    return new List<ProtoChatSessionInfo>();
                }

                var snapshot = JsonUtility.FromJson<ProtoChatSessionIndexSnapshot>(json);
                var sessions = snapshot?.sessions ?? Array.Empty<ProtoChatSessionInfo>();
                var list = new List<ProtoChatSessionInfo>();
                foreach (var session in sessions)
                {
                    if (session != null && !string.IsNullOrWhiteSpace(session.id))
                    {
                        list.Add(session);
                    }
                }

                list.Sort((a, b) => b.updatedAt.CompareTo(a.updatedAt));
                return list;
            }
            catch
            {
                return new List<ProtoChatSessionInfo>();
            }
        }

        public static void SaveSessionIndex(List<ProtoChatSessionInfo> sessions)
        {
            EnsureSessionFolder();
            var snapshot = new ProtoChatSessionIndexSnapshot
            {
                sessions = sessions == null || sessions.Count == 0
                    ? Array.Empty<ProtoChatSessionInfo>()
                    : sessions.ToArray()
            };
            var json = JsonUtility.ToJson(snapshot, true);
            File.WriteAllText(Path.GetFullPath(GetIndexPath()), json);
        }

        public static ProtoChatSession LoadSession(string sessionId)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                return null;
            }

            var path = Path.GetFullPath(GetSessionPath(sessionId));
            if (!File.Exists(path))
            {
                return null;
            }

            try
            {
                var json = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(json))
                {
                    return null;
                }

                return JsonUtility.FromJson<ProtoChatSession>(json);
            }
            catch
            {
                return null;
            }
        }

        public static void SaveSession(ProtoChatSession session)
        {
            if (session == null || string.IsNullOrWhiteSpace(session.id))
            {
                return;
            }

            EnsureSessionFolder();
            var json = JsonUtility.ToJson(session, true);
            File.WriteAllText(Path.GetFullPath(GetSessionPath(session.id)), json);
        }

        public static bool TryMigrateLegacyHistory(out ProtoChatSession migrated)
        {
            migrated = null;
            var legacyPath = Path.GetFullPath(ProtoChatHistoryStore.GetChatHistoryPath());
            if (!File.Exists(legacyPath))
            {
                return false;
            }

            try
            {
                var json = File.ReadAllText(legacyPath);
                if (string.IsNullOrWhiteSpace(json))
                {
                    File.Delete(legacyPath);
                    return false;
                }

                var snapshot = JsonUtility.FromJson<ProtoChatHistorySnapshot>(json);
                if (snapshot == null)
                {
                    File.Delete(legacyPath);
                    return false;
                }

                var hasMessages = snapshot.messages != null && snapshot.messages.Length > 0;
                var hasAgentHistory = snapshot.agentHistory != null && snapshot.agentHistory.Length > 0;
                var hasGoal = !string.IsNullOrWhiteSpace(snapshot.lastAgentGoal);
                if (!hasMessages && !hasAgentHistory && !hasGoal)
                {
                    File.Delete(legacyPath);
                    return false;
                }

                var now = NowMillis();
                migrated = new ProtoChatSession
                {
                    id = Guid.NewGuid().ToString("N"),
                    title = "Migrated Session",
                    createdAt = now,
                    updatedAt = now,
                    summary = string.Empty,
                    messages = snapshot.messages ?? Array.Empty<ProtoChatMessage>(),
                    lastAgentGoal = snapshot.lastAgentGoal ?? string.Empty,
                    agentHistory = snapshot.agentHistory ?? Array.Empty<string>()
                };

                SaveSession(migrated);
                File.Delete(legacyPath);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static long NowMillis()
        {
            return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }
    }
}
