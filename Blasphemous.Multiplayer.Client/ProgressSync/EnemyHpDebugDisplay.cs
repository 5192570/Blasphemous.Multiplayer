using System.Globalization;
using Framework.FrameworkCore;
using Framework.FrameworkCore.Attributes;
using Gameplay.GameControllers.Entities;
using UnityEngine;

namespace Blasphemous.Multiplayer.Client.ProgressSync;

/// <summary>
/// Attaches the temporary HP debug label to every enemy as it enters a scene.
/// </summary>
internal static class EnemyHpDebugDisplayManager
{
    internal static void Initialize()
    {
        // Opt-in only (EnemyDiagnostics.ShowEnemyHpLabels): a label costs a
        // TextMesh plus a per-frame LateUpdate on every enemy in the scene.
        if (!EnemyDiagnostics.ShowEnemyHpLabels)
            return;

        Entity.Started -= OnEntityStarted;
        Entity.Started += OnEntityStarted;
    }

    internal static void AttachExisting()
    {
        if (!EnemyDiagnostics.ShowEnemyHpLabels || !IsGameplayScene())
            return;

        foreach (Entity entity in Entity.LivingEntities)
            Attach(entity);
    }

    private static void OnEntityStarted(Entity entity)
    {
        Attach(entity);
    }

    private static void Attach(Entity entity)
    {
        if (!IsGameplayScene() || !(entity is Enemy enemy) || enemy == null ||
            !enemy.gameObject.activeInHierarchy || enemy.Status.Dead)
            return;

        EnemyHpDebugDisplay display = enemy.GetComponent<EnemyHpDebugDisplay>();
        if (display == null)
            display = enemy.gameObject.AddComponent<EnemyHpDebugDisplay>();

        display.Attach(enemy);
    }

    private static bool IsGameplayScene()
    {
        return Main.Multiplayer != null && Main.Multiplayer.CurrentlyInLevel;
    }
}

/// <summary>
/// Displays an enemy's native HP above it. This is diagnostic UI only and does not
/// read from or write to the multiplayer network.
/// </summary>
public sealed class EnemyHpDebugDisplay : MonoBehaviour
{
    private const float VerticalPadding = 0.4f;

    private Enemy _owner;
    private Life _life;
    private Transform _labelTransform;
    private TextMesh _text;

    internal void Attach(Enemy owner)
    {
        if (owner == null)
            return;

        if (_owner == owner)
        {
            Refresh();
            return;
        }

        Detach();
        _owner = owner;
        _life = owner.Stats.Life;
        _life.OnChanged += Refresh;
        _owner.OnDeath += Hide;
        _owner.OnDestroyEntity += Hide;

        CreateLabel();
        Refresh();
    }

    private void CreateLabel()
    {
        if (_labelTransform != null)
            return;

        GameObject labelObject = new GameObject("Multiplayer HP Debug Label");
        _labelTransform = labelObject.transform;
        _labelTransform.SetParent(transform, false);

        _text = labelObject.AddComponent<TextMesh>();
        Font font = Resources.GetBuiltinResource<Font>("Arial.ttf");
        if (font != null)
            _text.font = font;

        _text.anchor = TextAnchor.MiddleCenter;
        _text.alignment = TextAlignment.Center;
        _text.fontSize = 32;
        _text.characterSize = 0.08f;
        _text.fontStyle = FontStyle.Bold;
        _text.color = Color.yellow;

        MeshRenderer renderer = labelObject.GetComponent<MeshRenderer>();
        if (renderer != null)
            renderer.sortingOrder = 1000;
    }

    private void Refresh()
    {
        if (_owner == null || _text == null)
            return;

        // EntityName is the authored, human-readable name. Id (e.g. EV11) is
        // an internal identifier and should only be used as a last-resort fallback.
        string internalName = !string.IsNullOrEmpty(_owner.EntityName)
            ? _owner.EntityName
            : !string.IsNullOrEmpty(_owner.GetType().Name)
                ? _owner.GetType().Name
                : !string.IsNullOrEmpty(_owner.Id) ? _owner.Id : _owner.gameObject.name;

        _text.text = string.Format(
            CultureInfo.InvariantCulture,
            "{0}: {1:0.##}/{2:0.##}",
            internalName,
            _life.Current,
            _life.Final);
    }

    private void LateUpdate()
    {
        if (_owner == null || _labelTransform == null)
            return;

        Vector2 offset = _owner.healthOffset;
        if (_owner.Status.Orientation == EntityOrientation.Left)
            offset.x = -offset.x;

        _labelTransform.localPosition = new Vector3(offset.x, offset.y + VerticalPadding, 0f);

        Camera mainCamera = Camera.main;
        if (mainCamera != null)
            _labelTransform.rotation = mainCamera.transform.rotation;

        // Keep the text readable and at a consistent world size if the enemy flips or scales.
        Vector3 parentScale = transform.lossyScale;
        _labelTransform.localScale = new Vector3(
            InverseScale(parentScale.x),
            InverseScale(parentScale.y),
            InverseScale(parentScale.z));
    }

    private static float InverseScale(float value)
    {
        return Mathf.Abs(value) < 0.0001f ? 1f : 1f / value;
    }

    private void Hide()
    {
        if (_labelTransform != null)
            _labelTransform.gameObject.SetActive(false);
    }

    private void OnDestroy()
    {
        Detach();
    }

    private void Detach()
    {
        if (_life != null)
        {
            _life.OnChanged -= Refresh;
            _life = null;
        }

        if (_owner != null)
        {
            _owner.OnDeath -= Hide;
            _owner.OnDestroyEntity -= Hide;
            _owner = null;
        }
    }
}
