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
        /// <summary>
        /// Keyed by component kind and path. The kind is part of the key because a GameObject that
        /// has both a renderer and an animator (the clone's prompt art and body sprites, the
        /// enemy's own sprite) would otherwise collapse to one entry and the renderer - the thing
        /// that actually draws - would be the one overwritten.
        /// </summary>
        internal static Dictionary<string, string> Capture(Transform root, bool includePlayback = false)
        {
            Dictionary<string, string> state = new Dictionary<string, string>();
            if (root == null) return state;

            foreach (SpriteRenderer sprite in root.GetComponentsInChildren<SpriteRenderer>(true))
            {
                state["sprite|" + DescribePath(root, sprite.transform)] =
                    $"enabled={sprite.enabled} active={sprite.gameObject.activeInHierarchy} color=#{ColorUtility.ToHtmlStringRGBA(sprite.color)} mat={(sprite.sharedMaterial != null ? sprite.sharedMaterial.name : "null")} image={(sprite.sprite != null ? sprite.sprite.name : "null")} sorting={sprite.sortingLayerName}:{sprite.sortingOrder}";
            }

            foreach (ParticleSystem particles in root.GetComponentsInChildren<ParticleSystem>(true))
            {
                state["particles|" + DescribePath(root, particles.transform)] =
                    $"active={particles.gameObject.activeInHierarchy} playing={particles.isPlaying} alive={particles.particleCount}";
            }

            foreach (Animator animator in root.GetComponentsInChildren<Animator>(true))
            {
                string clip = DescribeClip(animator);
                string playback = string.Empty;
                if (includePlayback)
                {
                    AnimatorStateInfo info = animator.GetCurrentAnimatorStateInfo(0);
                    playback = $" normalized={info.normalizedTime:0.00}";
                }
                state["anim|" + DescribePath(root, animator.transform)] =
                    $"enabled={animator.enabled} active={animator.gameObject.activeInHierarchy} clip={clip}{playback} speed={animator.speed:0.00}";
            }

            return state;
        }

        /// <summary>The clip currently playing on an animator, or "null" when no state is active.</summary>
        internal static string DescribeClip(Animator animator)
        {
            if (animator == null) return "missing";
            AnimatorClipInfo[] clips = animator.GetCurrentAnimatorClipInfo(0);
            return clips.Length > 0 && clips[0].clip != null ? clips[0].clip.name : "null";
        }

        /// <summary>Everything that could draw right now, on one line, for a one-shot dump.</summary>
        internal static string Describe(Transform root)
        {
            if (root == null) return "none";
            Dictionary<string, string> state = Capture(root, true);
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
