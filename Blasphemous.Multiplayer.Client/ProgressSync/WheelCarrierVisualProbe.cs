using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace Blasphemous.Multiplayer.Client.ProgressSync
{
    /// <summary>
    /// Diagnostic only. Answers "which object is still drawing the WheelCarrier?" by capturing,
    /// for every renderer, particle system and animator in a hierarchy, both whether it is able
    /// to draw right now and what it would draw, then logging only the transitions while a death
    /// is being observed.
    ///
    /// It never changes a renderer, an animator or an active state.
    /// </summary>
    internal static class WheelCarrierVisualProbe
    {
        internal static Dictionary<string, string> Capture(Transform root)
        {
            Dictionary<string, string> state = new Dictionary<string, string>();
            if (root == null) return state;

            foreach (SpriteRenderer sprite in root.GetComponentsInChildren<SpriteRenderer>(true))
            {
                state[DescribePath(root, sprite.transform)] =
                    $"sprite enabled={sprite.enabled} active={sprite.gameObject.activeInHierarchy} color=#{ColorUtility.ToHtmlStringRGBA(sprite.color)} mat={(sprite.sharedMaterial != null ? sprite.sharedMaterial.name : "null")} image={(sprite.sprite != null ? sprite.sprite.name : "null")} sorting={sprite.sortingLayerName}:{sprite.sortingOrder}";
            }

            foreach (ParticleSystem particles in root.GetComponentsInChildren<ParticleSystem>(true))
            {
                state[DescribePath(root, particles.transform)] =
                    $"particles active={particles.gameObject.activeInHierarchy} playing={particles.isPlaying} alive={particles.particleCount}";
            }

            foreach (Animator animator in root.GetComponentsInChildren<Animator>(true))
            {
                AnimatorClipInfo[] clips = animator.GetCurrentAnimatorClipInfo(0);
                string clip = clips.Length > 0 && clips[0].clip != null ? clips[0].clip.name : "null";
                AnimatorStateInfo info = animator.GetCurrentAnimatorStateInfo(0);
                state[DescribePath(root, animator.transform)] =
                    $"animator enabled={animator.enabled} active={animator.gameObject.activeInHierarchy} clip={clip} normalized={info.normalizedTime:0.00} speed={animator.speed:0.00}";
            }

            return state;
        }

        /// <summary>Everything that could draw right now, on one line, for a one-shot dump.</summary>
        internal static string Describe(Transform root)
        {
            if (root == null) return "none";
            Dictionary<string, string> state = Capture(root);
            StringBuilder builder = new StringBuilder();
            builder.Append($"active={root.gameObject.activeInHierarchy} ");
            foreach (KeyValuePair<string, string> entry in state)
                builder.Append($"[{entry.Key} {entry.Value}] ");
            return builder.ToString();
        }

        /// <summary>Only the objects whose drawing state changed since the previous capture.</summary>
        internal static string Diff(Dictionary<string, string> before, Dictionary<string, string> after)
        {
            if (before == null || after == null) return null;

            StringBuilder builder = new StringBuilder();
            foreach (KeyValuePair<string, string> entry in after)
            {
                string previous;
                if (!before.TryGetValue(entry.Key, out previous))
                    builder.Append($"[{entry.Key} appeared {entry.Value}] ");
                else if (previous != entry.Value)
                    builder.Append($"[{entry.Key} {entry.Value} (was {previous})] ");
            }

            foreach (string removed in before.Keys)
                if (!after.ContainsKey(removed)) builder.Append($"[{removed} removed] ");

            return builder.Length == 0 ? null : builder.ToString();
        }

        private static string DescribePath(Transform root, Transform item)
        {
            if (item == root) return root.name;

            StringBuilder builder = new StringBuilder(item.name);
            Transform parent = item.parent;
            while (parent != null && parent != root)
            {
                builder.Insert(0, parent.name + "/");
                parent = parent.parent;
            }
            return builder.ToString();
        }
    }
}
