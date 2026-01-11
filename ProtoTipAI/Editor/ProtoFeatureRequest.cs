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
        public string status;
        public string[] dependsOn;
        public string notes;
        public string createdAt;
        public string updatedAt;
    }
}
