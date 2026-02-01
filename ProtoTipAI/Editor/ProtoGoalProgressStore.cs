#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace ProtoTipAI.Editor
{
    [Serializable]
    internal sealed class ProtoGoalProgressSnapshot
    {
        public string featureRequestId = string.Empty;
        public ProtoGoalProgressEntry[] goals = Array.Empty<ProtoGoalProgressEntry>();
        public long updatedAt;
    }

    [Serializable]
    internal sealed class ProtoGoalProgressEntry
    {
        public string goalId = string.Empty;
        // todo|in_progress|done|blocked
        public string status = "todo";
        public string lastError = string.Empty;
        public int attemptCount;
        public long startedAt;
        public long finishedAt;
    }

    /// <summary>
    /// Persists per-goal status across editor sessions.
    /// Stored as json in Library (via ProtoFeatureRequestStore folder).
    /// </summary>
    internal static class ProtoGoalProgressStore
    {
        private const string FileName = "GoalProgress.json";

        public static string GetPath(string featureRequestId)
        {
            var folder = ProtoFeatureRequestStore.EnsureFeatureRequestFolder();
            var safeId = string.IsNullOrWhiteSpace(featureRequestId) ? "default" : featureRequestId.Trim();
            return $"{folder}/{safeId}_{FileName}";
        }

        private static string ReadAllTextSafe(string path)
        {
            try
            {
                return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static void WriteAllTextSafe(string path, string content)
        {
            try
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                File.WriteAllText(path, content);
            }
            catch
            {
                // Intentionally swallow; persistence is best-effort.
            }
        }

        public static ProtoGoalProgressSnapshot Load(string featureRequestId)
        {
            var path = GetPath(featureRequestId);
            var json = ReadAllTextSafe(path);
            if (string.IsNullOrWhiteSpace(json))
            {
                return new ProtoGoalProgressSnapshot
                {
                    featureRequestId = featureRequestId,
                    goals = Array.Empty<ProtoGoalProgressEntry>(),
                    updatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                };
            }

            try
            {
                var snapshot = JsonUtility.FromJson<ProtoGoalProgressSnapshot>(json);
                if (snapshot == null)
                {
                    return new ProtoGoalProgressSnapshot
                    {
                        featureRequestId = featureRequestId,
                        goals = Array.Empty<ProtoGoalProgressEntry>(),
                        updatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                    };
                }

                snapshot.featureRequestId = featureRequestId;
                snapshot.goals = snapshot.goals ?? Array.Empty<ProtoGoalProgressEntry>();
                return snapshot;
            }
            catch
            {
                return new ProtoGoalProgressSnapshot
                {
                    featureRequestId = featureRequestId,
                    goals = Array.Empty<ProtoGoalProgressEntry>(),
                    updatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                };
            }
        }

        public static void Save(string featureRequestId, ProtoGoalProgressSnapshot snapshot)
        {
            var path = GetPath(featureRequestId);
            snapshot.featureRequestId = featureRequestId;
            snapshot.goals = snapshot.goals ?? Array.Empty<ProtoGoalProgressEntry>();
            snapshot.updatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            var json = JsonUtility.ToJson(snapshot, true);
            WriteAllTextSafe(path, json);
        }

        public static ProtoGoalProgressEntry GetOrCreate(ProtoGoalProgressSnapshot snapshot, string goalId)
        {
            snapshot.goals = snapshot.goals ?? Array.Empty<ProtoGoalProgressEntry>();

            for (int i = 0; i < snapshot.goals.Length; i++)
            {
                if (snapshot.goals[i] != null && snapshot.goals[i].goalId == goalId)
                    return snapshot.goals[i];
            }

            var entry = new ProtoGoalProgressEntry
            {
                goalId = goalId,
                status = "todo",
                attemptCount = 0,
                startedAt = 0,
                finishedAt = 0,
                lastError = string.Empty
            };

            var list = new List<ProtoGoalProgressEntry>(snapshot.goals);
            list.Add(entry);
            snapshot.goals = list.ToArray();
            return entry;
        }

        public static void SetStatus(ProtoGoalProgressEntry entry, string status, string? error = null)
        {
            entry.status = status;
            if (error != null)
                entry.lastError = error;

            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (status == "in_progress" && entry.startedAt == 0)
                entry.startedAt = now;
            if (status == "done" || status == "blocked")
                entry.finishedAt = now;
        }
    }
}
