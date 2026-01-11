using UnityEditor;

namespace ProtoTipAI.Editor
{
    internal static class ProtoOpenAISettings
    {
        private const string TokenKey = "ProtoTipAI.OpenAI.Token";
        private const string ModelKey = "ProtoTipAI.OpenAI.Model";
        private const string ProjectGoalKey = "ProtoTipAI.Project.Goal";
        private const string ProjectSummaryKey = "ProtoTipAI.Project.Summary";
        private const string DefaultModel = "gpt-5.2";

        public static string GetToken()
        {
            return EditorPrefs.GetString(TokenKey, string.Empty);
        }

        public static void SetToken(string token)
        {
            EditorPrefs.SetString(TokenKey, token ?? string.Empty);
        }

        public static string GetModel()
        {
            var model = EditorPrefs.GetString(ModelKey, DefaultModel);
            return string.IsNullOrWhiteSpace(model) ? DefaultModel : model;
        }

        public static void SetModel(string model)
        {
            EditorPrefs.SetString(ModelKey, model ?? string.Empty);
        }

        public static bool HasToken()
        {
            return !string.IsNullOrWhiteSpace(GetToken());
        }

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
