using System.Collections;
using UnityEngine;

public class CoroutineRunner : MonoBehaviour
{
    private static CoroutineRunner _instance;

    private static CoroutineRunner Instance
    {
        get
        {
            if (_instance != null) return _instance;

            CoroutineRunner existingRunner = FindFirstObjectByType<CoroutineRunner>();
            if (existingRunner != null)
            {
                _instance = existingRunner;
                DontDestroyOnLoad(_instance.gameObject);
                return _instance;
            }

            GameObject obj = new GameObject("CoroutineRunner");
            _instance = obj.AddComponent<CoroutineRunner>();
            DontDestroyOnLoad(obj);
            return _instance;
        }
    }

    private void Awake()
    {
        if (_instance == null)
        {
            _instance = this;
            DontDestroyOnLoad(gameObject);
            return;
        }

        if (_instance != this)
        {
            Destroy(gameObject);
        }
    }

    public static Coroutine Run(IEnumerator routine)
    {
        if (routine == null) return null;
        return Instance.StartCoroutine(routine);
    }

    public static void Stop(Coroutine routine)
    {
        if (_instance != null && routine != null) _instance.StopCoroutine(routine);
    }

    public static void StopAll()
    {
        if (_instance != null) _instance.StopAllCoroutines();
    }
}