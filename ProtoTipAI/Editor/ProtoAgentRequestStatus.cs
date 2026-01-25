#nullable enable
using System;

namespace ProtoTipAI.Editor
{
    internal enum ProtoAgentRequestStatus
    {
        Todo,
        InProgress,
        Done,
        Blocked
    }

    internal static class ProtoAgentRequestStatusExtensions
    {
        public static string ToNormalizedString(this ProtoAgentRequestStatus status)
        {
            return status switch
            {
                ProtoAgentRequestStatus.InProgress => "in_progress",
                ProtoAgentRequestStatus.Done => "done",
                ProtoAgentRequestStatus.Blocked => "blocked",
                _ => "todo"
            };
        }

        public static ProtoAgentRequestStatus ToStatus(this string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return ProtoAgentRequestStatus.Todo;
            }

            return value.Trim().ToLowerInvariant() switch
            {
                "in_progress" => ProtoAgentRequestStatus.InProgress,
                "done" => ProtoAgentRequestStatus.Done,
                "blocked" => ProtoAgentRequestStatus.Blocked,
                _ => ProtoAgentRequestStatus.Todo
            };
        }

        public static bool HasStatus(this ProtoFeatureRequest? request, ProtoAgentRequestStatus status)
        {
            if (request == null)
            {
                return false;
            }

            return string.Equals(request.status, status.ToNormalizedString(), StringComparison.OrdinalIgnoreCase);
        }

        public static void SetStatus(this ProtoFeatureRequest? request, ProtoAgentRequestStatus status)
        {
            if (request == null)
            {
                return;
            }

            request.status = status.ToNormalizedString();
        }
    }
}
