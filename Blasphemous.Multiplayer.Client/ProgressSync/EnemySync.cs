using System;
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
    private const float PeerStateGrace = 1.5f;
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
    private bool _entryEvaluated;
    private bool _awaitingPeerState;
    private float _awaitDeadline;
    private int _lastPeerSnapshotGeneration = -1;
    private float _lastPeerSnapshot = float.NegativeInfinity;
    private Vector3 _targetPosition;
    private GameObject _committedTarget;
    private float _targetCommitmentUntil;
    private bool _loggedFirstPeerSnapshot;

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

        if (!_entryEvaluated)
        {
            _entryEvaluated = true;
            _awaitingPeerState = true;
            _awaitDeadline = Time.unscaledTime + PeerStateGrace;
            ModLog.Info($"[EnemySync] Awaiting initial peer state for {_spawnPointId}");
        }

        SetAuthority(ResolveAuthority());
        if (_isAuthority && !_awaitingPeerState)
        {
            UpdateTarget();
            if (Time.unscaledTime >= _nextSnapshot)
            {
                _nextSnapshot = Time.unscaledTime + SnapshotInterval;
                Main.Multiplayer.NetworkManager.SendEnemyPosition(_sceneName, _spawnPointId, _generation, _enemy.transform.position,
                    (byte)_enemy.Status.Orientation, (byte)_enemy.entityCurrentState, _handler.IsWalking, _enemy.Stats.Life.Current);
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

    internal void NotePeerSnapshot(int generation)
    {
        if (generation != _generation)
        {
            ModLog.Warn($"[EnemySyncDiag] Ignored peer snapshot for different generation scene={_sceneName}, spawn={_spawnPointId}, localGeneration={_generation}, packetGeneration={generation}");
            return;
        }

        bool firstSnapshot = !_loggedFirstPeerSnapshot;
        bool peerWasStale = Time.unscaledTime - _lastPeerSnapshot >= PeerSnapshotTimeout;
        _lastPeerSnapshotGeneration = generation;
        _lastPeerSnapshot = Time.unscaledTime;
        if (firstSnapshot || peerWasStale)
        {
            _loggedFirstPeerSnapshot = true;
            ModLog.Info($"[EnemySyncDiag] {(firstSnapshot ? "First" : "Resumed")} peer snapshot scene={_sceneName}, spawn={_spawnPointId}, generation={_generation}, localPlayer={Main.Multiplayer?.PlayerName}, enemyInstance={_enemy?.GetInstanceID()}");
        }
    }

    internal void ApplySnapshot(Vector3 position, byte orientation, byte state, bool walking, float currentHealth)
    {
        if ((_isAuthority && !_awaitingPeerState) || _enemy == null || _handler == null) return;
        ApplyHealth(currentHealth);
        _targetPosition = position;
        _hasSnapshot = true;
        _handler.ApplySnapshot(orientation, state, walking);
    }

    private void ApplyHealth(float currentHealth)
    {
        if (_enemy == null || currentHealth <= 0f || float.IsNaN(currentHealth) || float.IsInfinity(currentHealth)) return;
        float maximumHealth = _enemy.Stats.Life.Final;
        if (maximumHealth > 0f) _enemy.SetHealth(Mathf.Min(currentHealth, maximumHealth));
    }

    private void RefreshIdentity()
    {
        if (EnemyDeathSyncManager.TryGetSyncIdentity(_enemy, out string scene, out string spawn, out int generation))
        {
            _sceneName = scene; _spawnPointId = spawn; _generation = generation;
        }
        else
        {
            _sceneName = null; _spawnPointId = null; _generation = -1;
        }
    }

    private bool HasPeerInScene()
    {
        string localName = Main.Multiplayer.PlayerName;
        foreach (PlayerStatus player in Main.Multiplayer.OtherPlayerManager.AllConnectedPlayers)
        {
            if (player == null || player.CurrentScene != _sceneName || string.IsNullOrEmpty(player.Name) || player.Name == localName) continue;
            return true;
        }
        return false;
    }

    private bool ResolveAuthority()
    {
        bool peerPublishing = _lastPeerSnapshotGeneration == _generation && Time.unscaledTime - _lastPeerSnapshot < PeerSnapshotTimeout;
        bool nameWins = IsLocalAuthority();
        if (_awaitingPeerState)
        {
            if (peerPublishing) { _awaitingPeerState = false; return false; }
            if (Time.unscaledTime < _awaitDeadline) return true;
            _awaitingPeerState = false;
            return !HasPeerInScene() || nameWins;
        }
        if (!HasPeerInScene()) return true;
        if (_isAuthority) return !(peerPublishing && !nameWins);
        return nameWins && !peerPublishing;
    }

    private string GetPreferredAuthorityName()
    {
        if (Main.Multiplayer == null || string.IsNullOrEmpty(Main.Multiplayer.PlayerName)) return "<unknown>";
        string ownerName = Main.Multiplayer.PlayerName;
        foreach (PlayerStatus player in Main.Multiplayer.OtherPlayerManager.AllConnectedPlayers)
        {
            if (player == null || player.CurrentScene != _sceneName || string.IsNullOrEmpty(player.Name)) continue;
            if (string.CompareOrdinal(player.Name, ownerName) < 0) ownerName = player.Name;
        }
        return ownerName;
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

    private bool IsLocalAuthority()
    {
        return Main.Multiplayer != null && GetPreferredAuthorityName() == Main.Multiplayer.PlayerName;
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
        string localName = Main.Multiplayer == null ? "<offline>" : Main.Multiplayer.PlayerName;
        string preferredName = Main.Multiplayer == null ? "<offline>" : GetPreferredAuthorityName();
        bool peerPublishing = _lastPeerSnapshotGeneration == _generation && Time.unscaledTime - _lastPeerSnapshot < PeerSnapshotTimeout;
        ModLog.Info($"[EnemySyncDiag] Authority transition scene={_sceneName}, spawn={_spawnPointId}, generation={_generation}, enemyInstance={_enemy?.GetInstanceID()}, localPlayer={localName}, preferredPlayer={preferredName}, result={(authority ? "LOCAL" : "FOLLOWER")}, peerPublishing={peerPublishing}, peerSnapshotAge={(peerPublishing ? (Time.unscaledTime - _lastPeerSnapshot).ToString("0.000") : "stale")}, awaitingPeerState={_awaitingPeerState}, peers={DescribePeers()}");
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
            ModLog.Warn($"[EnemyAttackSyncDiag] Native WheelCarrier attack observed but NOT SENT: local player is follower scene={_sceneName}, spawn={_spawnPointId}, generation={_generation}, enemyInstance={_enemy.GetInstanceID()}, localPlayer={Main.Multiplayer.PlayerName}, preferredPlayer={GetPreferredAuthorityName()}, peers={DescribePeers()}");
            return;
        }

        byte orientation = (byte)_enemy.Status.Orientation;
        ModLog.Info($"[EnemyAttackSync] Sending WheelCarrier attack scene={_sceneName}, spawn={_spawnPointId}, generation={_generation}, orientation={orientation}");
        Main.Multiplayer.NetworkManager.SendEnemyAttack(_sceneName, _spawnPointId, _generation, orientation);
    }

    internal bool IsExpectedAuthority(string playerName)
    {
        if (string.IsNullOrEmpty(playerName) || string.IsNullOrEmpty(_sceneName) || Main.Multiplayer == null)
            return false;

        string expected = Main.Multiplayer.PlayerName;
        foreach (PlayerStatus player in Main.Multiplayer.OtherPlayerManager.AllConnectedPlayers)
        {
            if (player == null || player.CurrentScene != _sceneName || string.IsNullOrEmpty(player.Name)) continue;
            if (string.IsNullOrEmpty(expected) || string.CompareOrdinal(player.Name, expected) < 0)
                expected = player.Name;
        }
        bool accepted = expected == playerName;
        if (!accepted)
            ModLog.Warn($"[EnemyAttackSyncDiag] Authority sender mismatch scene={_sceneName}, spawn={_spawnPointId}, generation={_generation}, sender={playerName}, expected={expected}, localPlayer={Main.Multiplayer.PlayerName}, peers={DescribePeers()}, localIsAuthority={_isAuthority}");
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
}