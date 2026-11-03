using System;
using System.Collections.Generic;

namespace Blasphemous.Multiplayer.Server;

/// <summary>
/// Tracks ordered scene membership and assigns one authoritative player per occupied scene.
/// The server owns this state; clients never elect a scene host independently.
/// </summary>
internal sealed class SceneAuthorityManager
{
    private sealed class SceneMember
    {
        internal readonly string Ip;
        internal readonly string Name;

        internal SceneMember(string ip, string name)
        {
            Ip = ip;
            Name = name;
        }
    }

    private sealed class SceneState
    {
        internal readonly List<SceneMember> Members = new List<SceneMember>();
        internal string HostIp;
        internal long Epoch;
    }

    internal sealed class Update
    {
        internal readonly string SceneName;
        internal readonly string HostName;
        internal readonly long Epoch;
        internal readonly List<string> Recipients;

        internal Update(string sceneName, string hostName, long epoch, List<string> recipients)
        {
            SceneName = sceneName;
            HostName = hostName;
            Epoch = epoch;
            Recipients = recipients;
        }
    }

    private readonly object _lock = new object();
    private readonly Dictionary<string, SceneState> _scenes = new Dictionary<string, SceneState>(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _playerScenes = new Dictionary<string, string>(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _lastEpochs = new Dictionary<string, long>(StringComparer.Ordinal);

    internal List<Update> Enter(string playerIp, string playerName, string sceneName)
    {
        List<Update> updates = new List<Update>();
        if (string.IsNullOrEmpty(playerIp) || string.IsNullOrEmpty(playerName) || string.IsNullOrEmpty(sceneName))
            return updates;

        lock (_lock)
        {
            if (_playerScenes.TryGetValue(playerIp, out string previousScene))
            {
                if (string.Equals(previousScene, sceneName, StringComparison.Ordinal))
                {
                    if (_scenes.TryGetValue(sceneName, out SceneState existing))
                        updates.Add(CreateUpdate(sceneName, existing, GetMemberIps(existing)));
                    return updates;
                }

                Update previousUpdate = RemovePlayer(playerIp, previousScene);
                if (previousUpdate != null)
                    updates.Add(previousUpdate);
            }

            if (!_scenes.TryGetValue(sceneName, out SceneState state))
            {
                state = new SceneState();
                _scenes.Add(sceneName, state);
            }

            state.Members.Add(new SceneMember(playerIp, playerName));
            _playerScenes[playerIp] = sceneName;

            if (string.IsNullOrEmpty(state.HostIp))
            {
                state.HostIp = playerIp;
                state.Epoch = NextEpoch(sceneName);
            }

            updates.Add(CreateUpdate(sceneName, state, GetMemberIps(state)));
        }

        return updates;
    }

    internal List<Update> Leave(string playerIp)
    {
        List<Update> updates = new List<Update>();
        if (string.IsNullOrEmpty(playerIp))
            return updates;

        lock (_lock)
        {
            if (!_playerScenes.TryGetValue(playerIp, out string sceneName))
                return updates;

            Update update = RemovePlayer(playerIp, sceneName);
            if (update != null)
                updates.Add(update);
        }

        return updates;
    }

    internal List<Update> GetSnapshots()
    {
        List<Update> snapshots = new List<Update>();
        lock (_lock)
        {
            foreach (KeyValuePair<string, SceneState> entry in _scenes)
            {
                if (!string.IsNullOrEmpty(entry.Value.HostIp))
                    snapshots.Add(CreateUpdate(entry.Key, entry.Value, new List<string>()));
            }
        }
        return snapshots;
    }

    internal bool IsHost(string playerIp, string sceneName, long epoch)
    {
        lock (_lock)
        {
            return _scenes.TryGetValue(sceneName, out SceneState state) &&
                state.Epoch == epoch &&
                string.Equals(state.HostIp, playerIp, StringComparison.Ordinal);
        }
    }

    private Update RemovePlayer(string playerIp, string sceneName)
    {
        if (!_scenes.TryGetValue(sceneName, out SceneState state))
        {
            _playerScenes.Remove(playerIp);
            return null;
        }

        List<string> recipients = GetMemberIps(state);
        bool wasHost = string.Equals(state.HostIp, playerIp, StringComparison.Ordinal);
        state.Members.RemoveAll(member => string.Equals(member.Ip, playerIp, StringComparison.Ordinal));
        _playerScenes.Remove(playerIp);

        if (!wasHost)
            return null;

        state.HostIp = state.Members.Count > 0 ? state.Members[0].Ip : null;
        state.Epoch = NextEpoch(sceneName);
        string hostName = state.Members.Count > 0 ? state.Members[0].Name : string.Empty;
        Update update = new Update(sceneName, hostName, state.Epoch, recipients);

        if (state.Members.Count == 0)
            _scenes.Remove(sceneName);

        return update;
    }

    private long NextEpoch(string sceneName)
    {
        long epoch = _lastEpochs.TryGetValue(sceneName, out long previous) ? previous + 1 : 1;
        _lastEpochs[sceneName] = epoch;
        return epoch;
    }

    private static List<string> GetMemberIps(SceneState state)
    {
        List<string> recipients = new List<string>();
        foreach (SceneMember member in state.Members)
            recipients.Add(member.Ip);
        return recipients;
    }

    private static Update CreateUpdate(string sceneName, SceneState state, List<string> recipients)
    {
        string hostName = string.Empty;
        if (!string.IsNullOrEmpty(state.HostIp))
        {
            foreach (SceneMember member in state.Members)
            {
                if (member.Ip == state.HostIp)
                {
                    hostName = member.Name;
                    break;
                }
            }
        }

        return new Update(sceneName, hostName, state.Epoch, recipients);
    }
}
