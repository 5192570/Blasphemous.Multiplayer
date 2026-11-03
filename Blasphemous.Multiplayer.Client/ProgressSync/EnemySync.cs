using System;
using Blasphemous.Multiplayer.Client.Network;
using System.Collections.Generic;
using Blasphemous.ModdingAPI;
using Blasphemous.Multiplayer.Client.Players;
using Framework.FrameworkCore;
using Framework.Managers;
using Gameplay.GameControllers.Entities;
using UnityEngine;

namespace Blasphemous.Multiplayer.Client.ProgressSync;

/// <summary>Shared authority, snapshot publication, and interpolation for supported enemies.</summary>
public sealed class EnemySync : MonoBehaviour
{
    private const float SnapshotInterval = 0.08f;
    private const float IdentityRefreshInterval = 0.5f;
    private const float PeerSnapshotTimeout = 1f;

    private EnemySyncHandler _handler;
    private Enemy _enemy;
    private string _sceneName;
    private string _spawnPointId;
    private int _generation;
    private float _nextSnapshot;
    private float _nextIdentityRefresh;
    private bool _isAuthority;
    private bool _authorityInitialized;
    internal bool IsAuthority => _isAuthority;
    private bool _hasSnapshot;
    private long _authorityEpoch = -1;
    private long _lastSnapshotRevision = -1;
    private int _lastPeerSnapshotGeneration = -1;
    private float _lastPeerSnapshot = float.NegativeInfinity;
    private Vector3 _targetPosition;
    private GameObject _committedTarget;
    private float _targetCommitmentUntil;
    private bool _loggedFirstPeerSnapshot;
    private string _lastAuthorityDiagnosticState;
    private float _nextAuthorityDiagnostic;

    internal void Attach(EnemySyncHandler handler)
    {
        if (handler == null || handler.Enemy == null) return;
        _handler = handler;
        _enemy = handler.Enemy;
        RefreshIdentity();
    }

    private void Update()
    {
        if (_enemy == null || _handler == null) return;
        _handler.OnSyncUpdate();
        if (Main.Multiplayer == null || Main.Multiplayer.NetworkManager == null || !Main.Multiplayer.NetworkManager.IsConnected)
        {
            SetAuthority(true);
            return;
        }

        if (Time.unscaledTime >= _nextIdentityRefresh)
        {
            _nextIdentityRefresh = Time.unscaledTime + IdentityRefreshInterval;
            RefreshIdentity();
        }
        if (string.IsNullOrEmpty(_sceneName) || string.IsNullOrEmpty(_spawnPointId) || _generation < 0) return;

        if (!SceneAuthorityState.TryGet(_sceneName, out string hostName, out long epoch) || string.IsNullOrEmpty(hostName))
        {
            _authorityEpoch = -1;
            SetAuthority(false);
            LogAuthorityContextIfChanged();
            return;
        }

        bool epochChanged = _authorityEpoch != epoch;
        _authorityEpoch = epoch;
        SetAuthority(string.Equals(Main.Multiplayer.PlayerName, hostName, StringComparison.Ordinal));
        if (epochChanged && !_isAuthority) ResetFollowerSnapshotBaseline("authority epoch changed");
        // A new authority namespace invalidates presentation copied from the previous one.
        if (epochChanged) _handler.OnAuthorityContextChanged("authority epoch changed");
        LogAuthorityContextIfChanged();
        if (_isAuthority)
        {
            UpdateTarget();
            if (Time.unscaledTime >= _nextSnapshot)
            {
                _nextSnapshot = Time.unscaledTime + SnapshotInterval;
                long snapshotRevision = EnemyDeathSyncManager.GetRevision(_sceneName, _spawnPointId, _generation, _authorityEpoch);
                if (_handler is WheelCarrierEnemySyncHandler)
                    SharedDiagnosticLogger.Info($"[WheelCarrierSnapshotDiag] Send scene={_sceneName}, spawn={_spawnPointId}, generation={_generation}, epoch={_authorityEpoch}, revision={snapshotRevision}, specialState={_handler.SpecialState}, time={Time.realtimeSinceStartup:0.000}");
                Main.Multiplayer.NetworkManager.SendEnemyPosition(_sceneName, _spawnPointId, _generation, _authorityEpoch, snapshotRevision, _enemy.transform.position,
                    (byte)_enemy.Status.Orientation, (byte)_enemy.entityCurrentState, _handler.SpecialState, _handler.IsWalking, _enemy.Stats.Life.Current);
            }
        }
        else if (!_isAuthority)
        {
            if (EnemyDeathSyncManager.TryTakePendingRemoteHealth(_enemy, _generation, out float pendingHealth))
                ApplyHealth(pendingHealth);
            if (!_hasSnapshot) return;
            float blend = 1f - Mathf.Exp(-18f * Time.deltaTime);
            _enemy.transform.position = Vector3.Lerp(_enemy.transform.position, _targetPosition, blend);
        }
    }

    internal bool NotePeerSnapshot(int generation, long revision)
    {
        if (generation != _generation)
        {
            SharedDiagnosticLogger.Warn($"[EnemySyncDiag] Ignored peer snapshot for different generation scene={_sceneName}, spawn={_spawnPointId}, localGeneration={_generation}, packetGeneration={generation}");
            return false;
        }
        if (revision < _lastSnapshotRevision)
        {
            SharedDiagnosticLogger.Warn($"[EnemyRevision] Ignored stale snapshot scene={_sceneName}, spawn={_spawnPointId}, generation={generation}, packetRevision={revision}, localRevision={_lastSnapshotRevision}");
            return false;
        }
        _lastSnapshotRevision = revision;

        bool firstSnapshot = !_loggedFirstPeerSnapshot;
        bool peerWasStale = Time.unscaledTime - _lastPeerSnapshot >= PeerSnapshotTimeout;
        _lastPeerSnapshotGeneration = generation;
        _lastPeerSnapshot = Time.unscaledTime;
        if (firstSnapshot || peerWasStale)
        {
            _loggedFirstPeerSnapshot = true;
            SharedDiagnosticLogger.Info($"[EnemySyncDiag] {(firstSnapshot ? "First" : "Resumed")} peer snapshot scene={_sceneName}, spawn={_spawnPointId}, generation={_generation}, localPlayer={Main.Multiplayer?.PlayerName}, enemyInstance={_enemy?.GetInstanceID()}");
        }
        return true;
    }

    internal void ApplySnapshot(Vector3 position, byte orientation, byte state, byte specialState, bool walking, float currentHealth)
    {
        if (_isAuthority || _enemy == null || _handler == null) return;
        if (_handler is WheelCarrierEnemySyncHandler wheelCarrierHandler)
            SharedDiagnosticLogger.Info($"[WheelCarrierSnapshotDiag] Receive/apply scene={_sceneName}, spawn={_spawnPointId}, generation={_generation}, epoch={_authorityEpoch}, revision={_lastSnapshotRevision}, incomingSpecialState={specialState}, localSpecialStateBefore={wheelCarrierHandler.SpecialState}, time={Time.realtimeSinceStartup:0.000}");
        _handler.ApplySnapshot(orientation, state, walking);
        _handler.ApplySpecialState(specialState, GetPreferredAuthorityName());
        if (_handler is WheelCarrierEnemySyncHandler appliedWheelCarrierHandler)
            SharedDiagnosticLogger.Info($"[WheelCarrierSnapshotDiag] Applied scene={_sceneName}, spawn={_spawnPointId}, generation={_generation}, epoch={_authorityEpoch}, revision={_lastSnapshotRevision}, incomingSpecialState={specialState}, localSpecialStateAfter={appliedWheelCarrierHandler.SpecialState}, time={Time.realtimeSinceStartup:0.000}");
        ApplyHealth(currentHealth);
        _targetPosition = position;
        _hasSnapshot = true;
    }

    private void ApplyHealth(float currentHealth)
    {
        if (_enemy == null || currentHealth <= 0f || float.IsNaN(currentHealth) || float.IsInfinity(currentHealth)) return;
        float maximumHealth = _enemy.Stats.Life.Final;
        if (maximumHealth > 0f) _enemy.SetHealth(Mathf.Min(currentHealth, maximumHealth));
    }

    private void RefreshIdentity()
    {
        string previousIdentity = _sceneName + "::" + _spawnPointId + "::" + _generation;
        if (EnemyDeathSyncManager.TryGetSyncIdentity(_enemy, out string scene, out string spawn, out int generation))
        {
            _sceneName = scene; _spawnPointId = spawn; _generation = generation;
        }
        else
        {
            _sceneName = null; _spawnPointId = null; _generation = -1;
        }
        string currentIdentity = _sceneName + "::" + _spawnPointId + "::" + _generation;
        if (previousIdentity != currentIdentity && !_isAuthority) ResetFollowerSnapshotBaseline("enemy identity changed");
    }

 
    private string GetPreferredAuthorityName()
    {
        if (Main.Multiplayer == null || string.IsNullOrEmpty(Main.Multiplayer.PlayerName)) return "<unknown>";
        if (SceneAuthorityState.TryGet(_sceneName, out string hostName, out _) && !string.IsNullOrEmpty(hostName))
            return hostName;
        return "<unassigned>";
    }

    private string DescribePeers()
    {
        if (Main.Multiplayer == null) return "<offline>";
        List<string> peers = new List<string>();
        foreach (PlayerStatus player in Main.Multiplayer.OtherPlayerManager.AllConnectedPlayers)
        {
            if (player == null) continue;
            peers.Add($"{player.Name}@{player.CurrentScene}");
        }
        return peers.Count == 0 ? "<none>" : string.Join(";", peers.ToArray());
    }

    private void LogAuthorityContextIfChanged()
    {
        if (Time.unscaledTime < _nextAuthorityDiagnostic) return;
        _nextAuthorityDiagnostic = Time.unscaledTime + 0.25f;

        string localName = Main.Multiplayer == null ? "<offline>" : Main.Multiplayer.PlayerName;
        string preferredName = Main.Multiplayer == null ? "<offline>" : GetPreferredAuthorityName();
        bool peerPublishing = _lastPeerSnapshotGeneration == _generation && Time.unscaledTime - _lastPeerSnapshot < PeerSnapshotTimeout;
        string state = $"local={localName}|preferred={preferredName}|authority={_isAuthority}|epoch={_authorityEpoch}|peerPublishing={peerPublishing}|peers={DescribePeers()}";
        if (state == _lastAuthorityDiagnosticState) return;

        _lastAuthorityDiagnosticState = state;
        SharedDiagnosticLogger.Info($"[EnemySyncDiag] Authority context changed scene={_sceneName}, spawn={_spawnPointId}, generation={_generation}, enemyInstance={_enemy?.GetInstanceID()}, {state}");
    }

 
    private void SetAuthority(bool authority)
    {
        if (_authorityInitialized && _isAuthority == authority) return;
        _authorityInitialized = true;
        _isAuthority = authority;
        _handler.SetAuthority(authority);
        _committedTarget = null;
        _targetCommitmentUntil = 0f;
        if (authority) _hasSnapshot = false;
        else ResetFollowerSnapshotBaseline("authority role changed");
        string localName = Main.Multiplayer == null ? "<offline>" : Main.Multiplayer.PlayerName;
        string preferredName = Main.Multiplayer == null ? "<offline>" : GetPreferredAuthorityName();
        bool peerPublishing = _lastPeerSnapshotGeneration == _generation && Time.unscaledTime - _lastPeerSnapshot < PeerSnapshotTimeout;
        SharedDiagnosticLogger.Info($"[EnemySyncDiag] Authority transition scene={_sceneName}, spawn={_spawnPointId}, generation={_generation}, enemyInstance={_enemy?.GetInstanceID()}, localPlayer={localName}, preferredPlayer={preferredName}, epoch={_authorityEpoch}, result={(authority ? "LOCAL" : "FOLLOWER")}, peerPublishing={peerPublishing}, peerSnapshotAge={(peerPublishing ? (Time.unscaledTime - _lastPeerSnapshot).ToString("0.000") : "stale")}, peers={DescribePeers()}");
    }

    private void UpdateTarget()
    {
        if (Core.Logic.Penitent == null) return;

        // Keep enforcing a valid target during the commitment window so the
        // enemy's own AI cannot silently replace it between sync updates.
        if (_committedTarget != null &&
            _committedTarget.activeInHierarchy &&
            Time.unscaledTime < _targetCommitmentUntil &&
            IsAvailableTarget(_committedTarget))
        {
            _handler.SetTarget(_committedTarget);
            return;
        }

        GameObject nearest = Core.Logic.Penitent.gameObject;
        float nearestDistance = Vector2.Distance(_enemy.transform.position, nearest.transform.position);
        foreach (OtherPlayerScript player in Main.Multiplayer.OtherPlayerManager.AllActivePlayers)
        {
            if (player == null || !player.gameObject.activeInHierarchy) continue;
            float distance = Vector2.Distance(_enemy.transform.position, player.transform.position);
            if (distance < nearestDistance) { nearestDistance = distance; nearest = player.gameObject; }
        }

        if (nearest == null || !nearest.activeInHierarchy) return;
        if (_committedTarget != nearest)
        {
            _committedTarget = nearest;
            _handler.SetTarget(nearest);
        }
        _targetCommitmentUntil = Time.unscaledTime + _handler.TargetCommitmentDuration;
    }

    internal void NotifyNativeAttackStarted()
    {
        if (_enemy == null || Main.Multiplayer == null || Main.Multiplayer.NetworkManager == null)
            return;
        if (!_isAuthority)
        {
            SharedDiagnosticLogger.Warn($"[EnemyAttackSyncDiag] Native WheelCarrier attack observed but NOT SENT: local player is follower scene={_sceneName}, spawn={_spawnPointId}, generation={_generation}, enemyInstance={_enemy.GetInstanceID()}, localPlayer={Main.Multiplayer.PlayerName}, preferredPlayer={GetPreferredAuthorityName()}, peers={DescribePeers()}");
            return;
        }

        byte orientation = (byte)_enemy.Status.Orientation;
        SharedDiagnosticLogger.Info($"[EnemyAttackSync] Sending WheelCarrier attack scene={_sceneName}, spawn={_spawnPointId}, generation={_generation}, orientation={orientation}");
        if (_authorityEpoch < 0)
        {
            SharedDiagnosticLogger.Warn($"[EnemyAttackSyncDiag] Attack not sent: scene authority assignment unavailable scene={_sceneName}, spawn={_spawnPointId}");
            return;
        }
        Main.Multiplayer.NetworkManager.SendEnemyAttack(_sceneName, _spawnPointId, _generation, _authorityEpoch,
            EnemyDeathSyncManager.GetRevision(_sceneName, _spawnPointId, _generation, _authorityEpoch), orientation);
    }

    internal bool IsExpectedAuthority(string playerName, long packetEpoch)
    {
        if (string.IsNullOrEmpty(playerName) || string.IsNullOrEmpty(_sceneName) || Main.Multiplayer == null ||
            !SceneAuthorityState.TryGet(_sceneName, out string expectedHost, out long currentEpoch))
            return false;

        bool accepted = packetEpoch == currentEpoch && string.Equals(expectedHost, playerName, StringComparison.Ordinal);
        if (!accepted)
            SharedDiagnosticLogger.Warn($"[EnemyAttackSyncDiag] Authority sender mismatch scene={_sceneName}, spawn={_spawnPointId}, generation={_generation}, sender={playerName}, expected={expectedHost}, packetEpoch={packetEpoch}, currentEpoch={currentEpoch}, localPlayer={Main.Multiplayer.PlayerName}, peers={DescribePeers()}, localIsAuthority={_isAuthority}");
        return accepted;
    }

    private bool IsAvailableTarget(GameObject target)
    {
        if (Core.Logic.Penitent != null && Core.Logic.Penitent.gameObject == target) return true;
        foreach (OtherPlayerScript player in Main.Multiplayer.OtherPlayerManager.AllActivePlayers)
        {
            if (player != null && player.gameObject == target && player.gameObject.activeInHierarchy) return true;
        }
        return false;
    }

    private void ResetFollowerSnapshotBaseline(string reason)
    {
        _hasSnapshot = false;
        _lastSnapshotRevision = -1;
        _lastPeerSnapshotGeneration = -1;
        _lastPeerSnapshot = float.NegativeInfinity;
        _targetPosition = _enemy == null ? Vector3.zero : _enemy.transform.position;
        SharedDiagnosticLogger.Info($"[EnemyRevision] Reset follower snapshot baseline: {reason}, scene={_sceneName}, spawn={_spawnPointId}, generation={_generation}, epoch={_authorityEpoch}");
    }
}
