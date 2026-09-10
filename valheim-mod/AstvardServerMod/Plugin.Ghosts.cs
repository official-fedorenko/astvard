using System.Collections.Generic;
using UnityEngine;

namespace AstvardServerMod
{
    public partial class Plugin
    {
        /// <summary>Where one piece of a planned build goes, and how it stands.</summary>
        private struct PiecePlacement
        {
            public GameObject Prefab;

            public Vector3 At;

            public Quaternion Turn;

            public PiecePlacement(GameObject prefab, Vector3 at, Quaternion turn)
            {
                Prefab = prefab;
                At = at;
                Turn = turn;
            }
        }

        /// <summary>
        /// The ghosts of one projection, kept by prefab and handed out again on every fresh
        /// look: a walk round a ring, or a plate slid along a wall, moves them rather than
        /// making new ones, which at a thousand pieces is the difference between smooth and
        /// not. Each is the real prefab, made without joining the world, with nothing in it
        /// left to collide or to run, and tinted - with one tinted copy of each material
        /// the projection uses rather than a copy for every ghost.
        /// </summary>
        private sealed class GhostPool
        {
            private readonly Dictionary<string, List<GameObject>> _ghosts = new Dictionary<string, List<GameObject>>();

            private readonly Dictionary<Material, Material> _tints = new Dictionary<Material, Material>();

            private readonly Color _tint;

            public GhostPool(Color tint)
            {
                _tint = tint;
            }

            public bool Empty
            {
                get { return _ghosts.Count == 0; }
            }

            /// <summary>Shows exactly these placements: ghosts are moved into place, made if short, hidden if spare.</summary>
            public void Show(List<PiecePlacement> placements)
            {
                var used = new Dictionary<string, int>();
                foreach (var placement in placements)
                {
                    var name = placement.Prefab.name;
                    if (!_ghosts.TryGetValue(name, out var pool))
                    {
                        pool = new List<GameObject>();
                        _ghosts[name] = pool;
                    }

                    used.TryGetValue(name, out var next);

                    // A ghost can go with its scene; a fresh one takes its place.
                    if (next < pool.Count && pool[next] == null) pool[next] = Make(placement.Prefab);
                    if (next == pool.Count) pool.Add(Make(placement.Prefab));

                    var ghost = pool[next];
                    used[name] = next + 1;
                    if (ghost == null) continue;

                    ghost.transform.SetPositionAndRotation(placement.At, placement.Turn);
                    if (!ghost.activeSelf) ghost.SetActive(true);
                }

                foreach (var entry in _ghosts)
                {
                    used.TryGetValue(entry.Key, out var shown);
                    for (var i = shown; i < entry.Value.Count; i++)
                        if (entry.Value[i] != null && entry.Value[i].activeSelf) entry.Value[i].SetActive(false);
                }
            }

            public void Clear()
            {
                foreach (var pool in _ghosts.Values)
                    foreach (var ghost in pool)
                        if (ghost != null) Object.Destroy(ghost);
                _ghosts.Clear();

                foreach (var tinted in _tints.Values)
                    if (tinted != null) Object.Destroy(tinted);
                _tints.Clear();
            }

            private GameObject Make(GameObject prefab)
            {
                // Without this the ghost would register itself as a real object the moment
                // it exists.
                ZNetView.m_forceDisableInit = true;
                try
                {
                    var ghost = Object.Instantiate(prefab);

                    foreach (var collider in ghost.GetComponentsInChildren<Collider>())
                        collider.enabled = false;
                    foreach (var behaviour in ghost.GetComponentsInChildren<MonoBehaviour>())
                        behaviour.enabled = false;

                    foreach (var renderer in ghost.GetComponentsInChildren<Renderer>())
                    {
                        var materials = renderer.sharedMaterials;
                        for (var i = 0; i < materials.Length; i++)
                        {
                            var original = materials[i];
                            if (original == null) continue;

                            if (!_tints.TryGetValue(original, out var tinted))
                            {
                                tinted = new Material(original);
                                if (tinted.HasProperty("_Color")) tinted.color = _tint;
                                _tints[original] = tinted;
                            }

                            materials[i] = tinted;
                        }

                        renderer.sharedMaterials = materials;
                    }

                    return ghost;
                }
                finally
                {
                    ZNetView.m_forceDisableInit = false;
                }
            }
        }

        /// <summary>Puts one piece in the world as the player's own, and notes it for taking down.</summary>
        private static void PlacePiece(GameObject prefab, Vector3 at, Quaternion turn, long creator,
                                       Splatform.PlatformUserID platform, List<ZDOID> built)
        {
            var go = Instantiate(prefab, at, turn);
            var piece = go.GetComponent<Piece>();
            if (piece != null) piece.SetCreator(creator, platform);

            var view = go.GetComponent<ZNetView>();
            if (view != null && view.IsValid()) built.Add(view.GetZDO().m_uid);
        }
    }
}
