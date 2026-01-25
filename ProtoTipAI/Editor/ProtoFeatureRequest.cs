using System;

namespace ProtoTipAI.Editor
{
    [Serializable]
    internal sealed class ProtoFeatureRequest
    {
        public string id;
        public string type;
        public string name;
        public string path;
        public string phaseId;
        public string phaseName;
        public string status;
        public string[] dependsOn;
        public string notes;
        public ProtoPlanStep[] steps;
        public string createdAt;
        public string updatedAt;
    }
}
