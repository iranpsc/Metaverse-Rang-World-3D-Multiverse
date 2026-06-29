using System;
using System.Collections.Generic;
using UnityEngine;

[CreateAssetMenu(fileName = "MetaverseNetworkPrefabRegistry", menuName = "Metaverse/Dedicated Server/Prefab Registry")]
public class MetaverseNetworkPrefabRegistry : ScriptableObject
{
    [Serializable]
    public class Entry
    {
        public string prefabId;
        public GameObject prefab;
    }

    [Header("Registered Prefabs")]
    [SerializeField] private List<Entry> prefabs = new List<Entry>();

    [Header("Debug")]
    [SerializeField] private bool logWarnings = true;

    private readonly Dictionary<string, GameObject> dictPrefabById = new Dictionary<string, GameObject>(StringComparer.Ordinal);
    private readonly Dictionary<GameObject, string> dictIdByPrefab = new Dictionary<GameObject, string>();
    private bool cacheBuilt;

    public int Count
    {
        get
        {
            EnsureCache();
            return dictPrefabById.Count;
        }
    }

    public IReadOnlyList<Entry> Entries => prefabs;

    public void RegisterPrefab(GameObject prefab)
    {
        if (prefab == null) return;
        RegisterPrefab(BuildPrefabId(prefab), prefab);
    }

    public void RegisterPrefab(string prefabId, GameObject prefab)
    {
        prefabId = SafeTrim(prefabId);
        if (string.IsNullOrWhiteSpace(prefabId) || prefab == null) return;

        Entry existing = prefabs.Find(x => string.Equals(SafeTrim(x.prefabId), prefabId, StringComparison.Ordinal));
        if (existing != null)
        {
            existing.prefab = prefab;
        }
        else
        {
            prefabs.Add(new Entry { prefabId = prefabId, prefab = prefab });
        }

        RebuildCache();
    }

    public void UnregisterPrefab(GameObject prefab)
    {
        if (prefab == null) return;
        EnsureCache();
        if (!dictIdByPrefab.TryGetValue(prefab, out string prefabId)) return;
        UnregisterPrefab(prefabId);
    }

    public void UnregisterPrefab(string prefabId)
    {
        prefabId = SafeTrim(prefabId);
        if (string.IsNullOrWhiteSpace(prefabId)) return;
        prefabs.RemoveAll(x => string.Equals(SafeTrim(x.prefabId), prefabId, StringComparison.Ordinal));
        RebuildCache();
    }

    public bool TryGetPrefab(string prefabId, out GameObject prefab)
    {
        EnsureCache();
        prefabId = SafeTrim(prefabId);
        return dictPrefabById.TryGetValue(prefabId, out prefab) && prefab != null;
    }

    public bool TryGetPrefabId(GameObject prefab, out string prefabId)
    {
        EnsureCache();
        if (prefab == null)
        {
            prefabId = string.Empty;
            return false;
        }

        if (dictIdByPrefab.TryGetValue(prefab, out prefabId)) return true;

        string cleanName = RemoveCloneSuffix(prefab.name);
        for (int i = 0; i < prefabs.Count; i++)
        {
            Entry entry = prefabs[i];
            if (entry == null || entry.prefab == null) continue;
            if (!string.Equals(entry.prefab.name, cleanName, StringComparison.Ordinal)) continue;
            prefabId = SafeTrim(entry.prefabId);
            return !string.IsNullOrWhiteSpace(prefabId);
        }

        prefabId = string.Empty;
        return false;
    }

    public bool ContainsPrefabId(string prefabId)
    {
        EnsureCache();
        prefabId = SafeTrim(prefabId);
        return dictPrefabById.ContainsKey(prefabId);
    }

    public List<string> GetPrefabIds()
    {
        EnsureCache();
        return new List<string>(dictPrefabById.Keys);
    }

    public void RebuildCache()
    {
        dictPrefabById.Clear();
        dictIdByPrefab.Clear();

        for (int i = 0; i < prefabs.Count; i++)
        {
            Entry entry = prefabs[i];
            if (entry == null || entry.prefab == null) continue;

            entry.prefabId = SafeTrim(entry.prefabId);
            if (string.IsNullOrWhiteSpace(entry.prefabId)) entry.prefabId = BuildPrefabId(entry.prefab);
            if (string.IsNullOrWhiteSpace(entry.prefabId)) continue;

            if (dictPrefabById.ContainsKey(entry.prefabId))
            {
                if (logWarnings) Debug.LogWarning($"[MetaverseNetworkPrefabRegistry] Duplicate prefabId ignored | prefabId={entry.prefabId}");
                continue;
            }

            dictPrefabById.Add(entry.prefabId, entry.prefab);
            if (!dictIdByPrefab.ContainsKey(entry.prefab)) dictIdByPrefab.Add(entry.prefab, entry.prefabId);

            MetaverseNetworkIdentity identity = entry.prefab.GetComponent<MetaverseNetworkIdentity>();
            if (identity != null && string.IsNullOrWhiteSpace(identity.PrefabId)) identity.AssignPrefabId(entry.prefabId);
        }

        cacheBuilt = true;
    }

    public void ValidateRegistry()
    {
        RebuildCache();
        for (int i = 0; i < prefabs.Count; i++)
        {
            Entry entry = prefabs[i];
            if (entry == null || entry.prefab == null)
            {
                if (logWarnings) Debug.LogWarning($"[MetaverseNetworkPrefabRegistry] Empty entry | index={i}");
                continue;
            }

            if (entry.prefab.GetComponent<MetaverseNetworkIdentity>() == null)
            {
                if (logWarnings) Debug.LogWarning($"[MetaverseNetworkPrefabRegistry] Prefab has no MetaverseNetworkIdentity | prefab={entry.prefab.name}");
            }
        }
    }

    private void EnsureCache()
    {
        if (!cacheBuilt) RebuildCache();
    }

    private string BuildPrefabId(GameObject prefab)
    {
        if (prefab == null) return string.Empty;
        return RemoveCloneSuffix(prefab.name).Trim().ToLowerInvariant().Replace(" ", "_");
    }

    private string RemoveCloneSuffix(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        return value.Replace("(Clone)", string.Empty).Trim();
    }

    private string SafeTrim(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
    }

#if UNITY_EDITOR
    private void OnValidate()
    {
        cacheBuilt = false;
    }
#endif
}
