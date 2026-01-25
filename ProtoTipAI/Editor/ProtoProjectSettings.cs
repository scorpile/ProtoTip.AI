using UnityEditor;

namespace ProtoTipAI.Editor
{
    internal static class ProtoProjectSettings
    {
        private const string ProjectGoalKey = "ProtoTipAI.Project.Goal";
        private const string ProjectSummaryKey = "ProtoTipAI.Project.Summary";

        public static string GetProjectGoal()
        {
            return EditorPrefs.GetString(ProjectGoalKey, string.Empty);
        }

        public static void SetProjectGoal(string goal)
        {
            EditorPrefs.SetString(ProjectGoalKey, goal ?? string.Empty);
        }

        public static string GetProjectSummary()
        {
            return EditorPrefs.GetString(ProjectSummaryKey, string.Empty);
        }

        public static void SetProjectSummary(string summary)
        {
            EditorPrefs.SetString(ProjectSummaryKey, summary ?? string.Empty);
        }
    }
}
