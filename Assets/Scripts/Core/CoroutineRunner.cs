using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;


public class CoroutineRunner : MonoBehaviour
{
    private static CoroutineRunner _instance;

    public static Coroutine Run(IEnumerator routine)
    {
        if (_instance == null)
        {
            GameObject obj = new GameObject("CoroutineRunner");
            _instance = obj.AddComponent<CoroutineRunner>();
            GameObject.DontDestroyOnLoad(obj);
        }

        return _instance.StartCoroutine(routine);
    }

    // اضافه به انتهای کلاس CoroutineRunner
    public static void Stop(Coroutine routine)
    {
        if (_instance != null && routine != null)
            _instance.StopCoroutine(routine);
    }

    public static void StopAll()
    {
        if (_instance != null)
            _instance.StopAllCoroutines();
    }
}
