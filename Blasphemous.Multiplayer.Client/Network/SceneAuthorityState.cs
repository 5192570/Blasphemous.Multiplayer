using System;
using System.Collections.Generic;
using Blasphemous.ModdingAPI;

namespace Blasphemous.Multiplayer.Client.Network;

/// <summary>
/// Client-side cache of server-issued scene ownership, used to validate host authority
/// and stamp authoritative enemy damage and death operations.
/// </summary>
internal static class SceneAuthorityState
{
    private sealed class Assignment
    {
        internal readonly string HostName;
        internal readonly long Epoch;

        internal Assignment(string hostName, long epoch)
        {
            HostName = hostName;
            Epoch = epoch;
        }
    }

    private static readonly Dictionary<string, Assignment> assignments =
        new Dictionary<string, Assignment>(StringComparer.Ordinal);

    internal static void Receive(string sceneName, string hostName, long epoch)
    {
        if (string.IsNullOrEmpty(sceneName) || epoch < 0)
            return;

        if (assignments.TryGetValue(sceneName, out Assignment current))
        {
            if (epoch < current.Epoch)
            {
                SharedDiagnosticLogger.Warn($"[SceneAuthority] Ignored stale assignment scene={sceneName}, incomingHost={hostName}, incomingEpoch={epoch}, currentHost={current.HostName}, currentEpoch={current.Epoch}");
                return;
            }

            if (epoch == current.Epoch && !string.Equals(hostName, current.HostName, StringComparison.Ordinal))
            {
                SharedDiagnosticLogger.Warn($"[SceneAuthority] Ignored conflicting assignment at same epoch scene={sceneName}, incomingHost={hostName}, currentHost={current.HostName}, epoch={epoch}");
                return;
            }
        }

        assignments[sceneName] = new Assignment(hostName ?? string.Empty, epoch);
        SharedDiagnosticLogger.Info($"[SceneAuthority] Assignment received scene={sceneName}, host={(string.IsNullOrEmpty(hostName) ? "<none>" : hostName)}, epoch={epoch}");
    }

    internal static bool TryGet(string sceneName, out string hostName, out long epoch)
    {
        hostName = string.Empty;
        epoch = -1;
        if (string.IsNullOrEmpty(sceneName) || !assignments.TryGetValue(sceneName, out Assignment assignment))
            return false;

        hostName = assignment.HostName;
        epoch = assignment.Epoch;
        return true;
    }

    internal static void ResetSession()
    {
        assignments.Clear();
    }
}
