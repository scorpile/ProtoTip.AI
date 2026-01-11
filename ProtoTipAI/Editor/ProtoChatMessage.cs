using System;

namespace ProtoTipAI.Editor
{
    [Serializable]
    internal sealed class ProtoChatMessage
    {
        public string role;
        public string content;
    }
}
