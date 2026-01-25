using System;

namespace ProtoTipAI.Editor
{
    internal static class ProtoChatHistoryStore
    {
        public const string ChatHistoryFileName = "ChatHistory.json";

        public static string GetChatHistoryPath()
        {
            var folder = ProtoFeatureRequestStore.EnsureFeatureRequestFolder();
            return $"{folder}/{ChatHistoryFileName}";
        }
    }

    [Serializable]
    internal sealed class ProtoChatHistorySnapshot
    {
        public ProtoChatMessage[] messages;
        public string lastAgentGoal;
        public string[] agentHistory;
    }
}
