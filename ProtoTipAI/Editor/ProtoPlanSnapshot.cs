namespace ProtoTipAI.Editor
{
    internal static class ProtoPlanStorage
    {
        public const string PlanRawFileName = "PlanRaw.json";

        public static string GetPlanRawPath()
        {
            var folder = ProtoFeatureRequestStore.EnsureFeatureRequestFolder();
            return $"{folder}/{PlanRawFileName}";
        }
    }

    [System.Serializable]
    internal sealed class ProtoPlanStep
    {
        public string id;
        public string type;
        public string name;
        public string path;
        public string[] dependsOn;
        public string notes;
    }

    [System.Serializable]
    internal sealed class ProtoPhasePlan
    {
        public ProtoPlanPhase[] phases;
    }

    [System.Serializable]
    internal sealed class ProtoPlanPhase
    {
        public string id;
        public string name;
        public string goal;
        public string overview;
        public ProtoFeatureRequest[] featureRequests;
    }

    [System.Serializable]
    internal sealed class ProtoGenerationPlan
    {
        public ProtoFeatureRequest[] featureRequests;
    }
}
