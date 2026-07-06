using System.Collections.Generic;
using UnityEngine;
using Mirror;
using UnityEngine.SceneManagement;

[AddComponentMenu("Meta/Interest Management")]
public class Meta_InterestManagement : InterestManagement
{
    [Tooltip("The maximum range that objects will be visible at. Add DistanceInterestManagementCustomRange onto NetworkIdentities for custom ranges.")]
    [Range(0f, 1000f)]
    public int Range = 250;

    [Tooltip("Rebuild all every 'rebuildInterval' seconds.")]
    public float RefreshRate = 1f;

    [Tooltip("Objects that will always remain visible and never get disabled.")]
    public List<NetworkIdentity> ExcludeObject = new List<NetworkIdentity>();

    private double LastRefreshTime;
    private readonly Dictionary<NetworkIdentity, DistanceInterestManagementCustomRange> CustomRanges = new();

    private bool SceneLoaded;

    [ServerCallback]
    void Awake()
    {
        // Listen to scene change events so we can refresh after lobby loads
        SceneManager.sceneLoaded += OnSceneLoaded;
    }

    [ServerCallback]
    void OnDestroy()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
    }

    [ServerCallback]
    void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        SceneLoaded = true;
    }

    [ServerCallback]
    void Update()
    {
        // When new scene loads, refresh exclude list
        if (SceneLoaded && NetworkServer.active)
        {
            RefreshExcludedObjects();
            SceneLoaded = false;
        }
    }

    [ServerCallback]
    void RefreshExcludedObjects()
    {
        ExcludeObject.Clear();

        // Automatically find all objects with tag "AlwaysVisible"
        GameObject[] _Found = GameObject.FindGameObjectsWithTag("ExcludeInterestManagement");

        foreach (GameObject _Obj in _Found)
        {
            if (_Obj.TryGetComponent(out NetworkIdentity _Id))
                ExcludeObject.Add(_Id);
        }

        Debug.Log($"[InterestManager] Found {ExcludeObject.Count} excluded objects in scene '{SceneManager.GetActiveScene().name}'");
    }

    [ServerCallback]
    int GetVisRange(NetworkIdentity identity)
    {
        return CustomRanges.TryGetValue(identity, out DistanceInterestManagementCustomRange _Custom) ? _Custom.visRange : Range;
    }

    [ServerCallback]
    public override bool OnCheckObserver(NetworkIdentity identity, NetworkConnectionToClient newObserver)
    {
        if (ExcludeObject.Contains(identity))
            return true;

        int _Range = GetVisRange(identity);
        if (newObserver == null || newObserver.identity == null)
            return false;

        return Vector3.Distance(identity.transform.position, newObserver.identity.transform.position) < _Range;
    }

    [ServerCallback]
    public override void OnRebuildObservers(NetworkIdentity identity, HashSet<NetworkConnectionToClient> newObservers)
    {
        if (ExcludeObject.Contains(identity))
        {
            foreach (NetworkConnectionToClient _Conn in NetworkServer.connections.Values)
            {
                if (_Conn != null && _Conn.isAuthenticated)
                    newObservers.Add(_Conn);
            }
            return;
        }

        int _Range = GetVisRange(identity);
        Vector3 _Position = identity.transform.position;

        foreach (NetworkConnectionToClient _Conn in NetworkServer.connections.Values)
        {
            if (_Conn == null || _Conn.identity == null || !_Conn.isAuthenticated)
                continue;

            if (ExcludeObject.Contains(_Conn.identity))
                continue;

            if (Vector3.Distance(_Conn.identity.transform.position, _Position) < _Range)
                newObservers.Add(_Conn);
        }
    }

    [ServerCallback]
    public override void OnSpawned(NetworkIdentity identity)
    {
        if (identity.TryGetComponent(out DistanceInterestManagementCustomRange _Custom))
            CustomRanges[identity] = _Custom;
    }

    [ServerCallback]
    public override void OnDestroyed(NetworkIdentity identity)
    {
        CustomRanges.Remove(identity);
    }

    [ServerCallback]
    public override void SetHostVisibility(NetworkIdentity identity, bool visible)
    {
        if (ExcludeObject.Contains(identity))
            return;

        base.SetHostVisibility(identity, visible);
    }

    [ServerCallback]
    public override void ResetState()
    {
        CustomRanges.Clear();
    }

    [ServerCallback]
    void LateUpdate()
    {
        if (NetworkTime.localTime >= LastRefreshTime + RefreshRate)
        {
            RebuildAll();
            LastRefreshTime = NetworkTime.localTime;
        }
    }
}
