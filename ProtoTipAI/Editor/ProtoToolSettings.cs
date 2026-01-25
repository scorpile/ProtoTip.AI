using UnityEditor;

namespace ProtoTipAI.Editor
{
    internal static class ProtoToolSettings
    {
        private const string AutoConfirmKey = "ProtoTipAI.Tools.AutoConfirm";
        private const string FullAgentKey = "ProtoTipAI.Tools.FullAgent";

        public static bool GetAutoConfirm()
        {
            return EditorPrefs.GetBool(AutoConfirmKey, false);
        }

        public static void SetAutoConfirm(bool value)
        {
            EditorPrefs.SetBool(AutoConfirmKey, value);
        }

        public static bool GetFullAgentMode()
        {
            return EditorPrefs.GetBool(FullAgentKey, false);
        }

        public static void SetFullAgentMode(bool value)
        {
            EditorPrefs.SetBool(FullAgentKey, value);
        }
    }
}
