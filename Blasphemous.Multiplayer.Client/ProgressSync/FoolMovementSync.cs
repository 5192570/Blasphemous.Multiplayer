using System;
using Blasphemous.ModdingAPI;
using Blasphemous.Multiplayer.Client.Players;
using Framework.FrameworkCore;
using Framework.Managers;
using Gameplay.GameControllers.Enemies.Fool;
using Gameplay.GameControllers.Entities;
using UnityEngine;

namespace Blasphemous.Multiplayer.Client.ProgressSync;

internal static class FoolMovementSyncManager
{
    internal static void Initialize()
    {
        Entity.Started -= OnEntityStarted;
        Entity.Started += OnEntityStarted;
    }

    internal static void AttachExisting()
    {
        foreach (Entity entity in Entity.LivingEntities) Attach(entity);
    }

    private static void OnEntityStarted(Entity entity) => Attach(entity);

    private static void Attach(Entity entity)
    {
        if (!(entity is Fool fool) || fool == null || fool.Status.Dead) return;
        FoolMovementSync sync = fool.GetComponent<FoolMovementSync>();
        if (sync == null) sync = fool.gameObject.AddComponent<FoolMovementSync>();
        sync.Attach(fool);
    }
}

/// <summary>Replicates Fool movement from one deterministic peer and makes other peers follow snapshots.</summary>
public sealed class FoolMovementSync : MonoBehaviour
{
    private const float SnapshotInterval = 0.08f;
    private const float IdentityRefreshInterval = 0.5f;
    // Give the remote player's EnterScene notification and first enemy snapshot time to arrive.
    // This wait is unconditional: CurrentScene may still be empty when this Fool is first updated.
    private const float PeerStateGrace = 1.5f;
    // A peer counts as actively publishing this exact enemy generation if a snapshot arrived recently.
    private const float PeerSnapshotTimeout = 1f;
    private Fool _fool;
    private string _sceneName;
    private string _spawnPointId;
    private int _generation;
    private float _nextSnapshot;
    private float _nextIdentityRefresh;
    private bool _isAuthority;
    private bool _authorityInitialized;
    private bool _hasSnapshot;
    private bool _entryEvaluated;
    private bool _awaitingPeerState;
    private float _awaitDeadline;
    private int _lastPeerSnapshotGeneration = -1;
    private float _lastPeerSnapshot = float.NegativeInfinity;
    private Vector3 _targetPosition;

    internal void Attach(Fool fool)
    {
        if (fool == null) return;
        _fool = fool;
        RefreshIdentity();
    }

    private void Update()
    {
        if (_fool == null) return;
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
            // Always wait briefly before publishing. A returning player may not yet know the
            // incumbent's CurrentScene because its EnterScene packet is processed asynchronously.
            // The incumbent can still send snapshots during this period, which we accept below.
            _entryEvaluated = true;
            _awaitingPeerState = true;
            _awaitDeadline = Time.unscaledTime + PeerStateGrace;
            ModLog.Info($"[FoolMovementSync] Awaiting initial peer state for {_spawnPointId}");
        }

        SetAuthority(ResolveAuthority());
        if (_isAuthority && !_awaitingPeerState)
        {
            UpdateTarget();
            if (Time.unscaledTime >= _nextSnapshot)
            {
                _nextSnapshot = Time.unscaledTime + SnapshotInterval;
                bool walking = _fool.Input != null && Mathf.Abs(_fool.Input.HorizontalInput) > 0.01f;
                Main.Multiplayer.NetworkManager.SendEnemyPosition(_sceneName, _spawnPointId, _generation, _fool.transform.position,
                    (byte)_fool.Status.Orientation, (byte)_fool.entityCurrentState, walking, _fool.Stats.Life.Current);
            }
        }
        else if (!_isAuthority)
        {
            if (EnemyDeathSyncManager.TryTakePendingRemoteHealth(_fool, _generation, out float pendingHealth))
                ApplyHealth(pendingHealth);

            if (!_hasSnapshot) return;
            float blend = 1f - Mathf.Exp(-18f * Time.deltaTime);
            _fool.transform.position = Vector3.Lerp(_fool.transform.position, _targetPosition, blend);
        }
    }

    /// <summary>Records a snapshot for this exact enemy generation, even if it is not applied.</summary>
    internal void NotePeerSnapshot(int generation)
    {
        if (generation != _generation) return;
        _lastPeerSnapshotGeneration = generation;
        _lastPeerSnapshot = Time.unscaledTime;
    }

    internal void ApplySnapshot(Vector3 position, byte orientation, byte state, bool walking, float currentHealth)
    {
        // While awaiting the peer's state this client is not yet authoritative, so it accepts the snapshot.
        if ((_isAuthority && !_awaitingPeerState) || _fool == null) return;
        ApplyHealth(currentHealth);
        _targetPosition = position;
        _hasSnapshot = true;
        _fool.SetOrientation((EntityOrientation)orientation);
        _fool.entityCurrentState = (EntityStates)state;
        if (_fool.AnimatorInyector != null)
        {
            if (walking && state != (byte)EntityStates.Hurt) _fool.AnimatorInyector.Walk();
            else _fool.AnimatorInyector.StopWalk();
        }
    }

    private void ApplyHealth(float currentHealth)
    {
        if (_fool == null || currentHealth <= 0f || float.IsNaN(currentHealth) || float.IsInfinity(currentHealth)) return;
        float maximumHealth = _fool.Stats.Life.Final;
        if (maximumHealth <= 0f) return;
        _fool.SetHealth(Mathf.Min(currentHealth, maximumHealth));
    }

    private void RefreshIdentity()
    {
        if (EnemyDeathSyncManager.TryGetSyncIdentity(_fool, out string scene, out string spawn, out int generation))
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

    /// <summary>
    /// Name ordering alone lets a returning player take over with a fresh full-health Fool. Instead the
    /// player who already holds the Fool keeps authority, and a newcomer follows until the peer stops publishing.
    /// Name ordering only breaks ties (both entered together, or both respawned by a Prie Dieu reset).
    /// </summary>
    private bool ResolveAuthority()
    {
        bool peerPublishing = _lastPeerSnapshotGeneration == _generation &&
            Time.unscaledTime - _lastPeerSnapshot < PeerSnapshotTimeout;
        bool nameWins = IsLocalAuthority();

        if (_awaitingPeerState)
        {
            // A matching snapshot is stronger evidence than the asynchronous scene roster.
            if (peerPublishing) { _awaitingPeerState = false; return false; }
            // Keep the local AI untouched while waiting, but publish nothing.
            if (Time.unscaledTime < _awaitDeadline) return true;
            _awaitingPeerState = false;
            return !HasPeerInScene() || nameWins;
        }

        if (!HasPeerInScene()) return true;

        // Incumbent: yield only if a peer is also publishing and wins the name tie-break.
        if (_isAuthority) return !(peerPublishing && !nameWins);
        // Follower: keep following while the peer publishes; otherwise fall back to name ordering.
        return nameWins && !peerPublishing;
    }

    private bool IsLocalAuthority()
    {
        string localName = Main.Multiplayer.PlayerName;
        if (string.IsNullOrEmpty(localName)) return false;
        string ownerName = localName;
        foreach (PlayerStatus player in Main.Multiplayer.OtherPlayerManager.AllConnectedPlayers)
        {
            if (player == null || player.CurrentScene != _sceneName || string.IsNullOrEmpty(player.Name)) continue;
            if (string.CompareOrdinal(player.Name, ownerName) < 0) ownerName = player.Name;
        }
        return ownerName == localName;
    }

    private void SetAuthority(bool authority)
    {
        if (_authorityInitialized && _isAuthority == authority) return;
        _authorityInitialized = true;
        _isAuthority = authority;
        if (_fool.Behaviour != null)
        {
            _fool.Behaviour.EnableBehaviourOnLoad = authority;
            if (!authority)
            {
                _fool.Behaviour.StopBehaviour();
                _fool.Behaviour.StopMovement();
            }
        }
        if (authority) _hasSnapshot = false;
        ModLog.Info($"[FoolMovementSync] Authority {(authority ? "local" : "remote-following")} for {_spawnPointId}");
    }

    private void UpdateTarget()
    {
        if (Core.Logic.Penitent == null) return;
        GameObject nearest = Core.Logic.Penitent.gameObject;
        float nearestDistance = Vector2.Distance(_fool.transform.position, nearest.transform.position);
        foreach (OtherPlayerScript player in Main.Multiplayer.OtherPlayerManager.AllActivePlayers)
        {
            if (player == null) continue;
            float distance = Vector2.Distance(_fool.transform.position, player.transform.position);
            if (distance < nearestDistance) { nearestDistance = distance; nearest = player.gameObject; }
        }
        _fool.Target = nearest;
    }
}
