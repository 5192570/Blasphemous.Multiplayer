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
    private Fool _fool;
    private string _sceneName;
    private string _spawnPointId;
    private int _generation;
    private float _nextSnapshot;
    private float _nextIdentityRefresh;
    private bool _isAuthority;
    private bool _authorityInitialized;
    private bool _hasSnapshot;
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

        SetAuthority(IsLocalAuthority());
        if (_isAuthority)
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
        else
        {
            if (EnemyDeathSyncManager.TryTakePendingRemoteHealth(_fool, _generation, out float pendingHealth))
                ApplyHealth(pendingHealth);

            if (!_hasSnapshot) return;
            float blend = 1f - Mathf.Exp(-18f * Time.deltaTime);
            _fool.transform.position = Vector3.Lerp(_fool.transform.position, _targetPosition, blend);
        }
    }

    internal void ApplySnapshot(Vector3 position, byte orientation, byte state, bool walking, float currentHealth)
    {
        if (_isAuthority || _fool == null) return;
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
