using System;
using System.Collections;
using UnityEngine;
using UnityEngine.UI;

#if UNITY_EDITOR
using UnityEditor;
#endif

#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
using Network_A.Core;
#endif

public class ExitGameButton : MonoBehaviour
{
    [SerializeField] private Button btnExit;
    [SerializeField] private float quitTimeoutSeconds = 2.5f;

    private bool isQuitting;

    //* Binds the exit button when the object wakes up.
    private void Awake()
    {
        if (btnExit == null) btnExit = GetComponent<Button>();
        if (btnExit != null) btnExit.onClick.AddListener(ExitGame);
    }

    //* Starts safe exit flow for editor and Windows build.
    public void ExitGame()
    {
        if (isQuitting) return;

        isQuitting = true;
        if (btnExit != null) btnExit.interactable = false;

#if UNITY_EDITOR
        EditorApplication.isPlaying = false;
#else
        StartCoroutine(QuitRoutine());
#endif
    }

    //* Closes native network resources before quitting the application.
    private IEnumerator QuitRoutine()
    {
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
        bool shutdownFinished = false;

        var shutdownTask = GrpcNativeUnaryClient.ShutdownAsync();
        shutdownTask.ContinueWith(_ => shutdownFinished = true);

        float timer = 0f;

        while (!shutdownFinished && timer < quitTimeoutSeconds)
        {
            timer += Time.unscaledDeltaTime;
            yield return null;
        }
#endif

        Application.Quit();

#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
        yield return new WaitForSecondsRealtime(0.5f);
        Environment.Exit(0);
#else
        yield return null;
#endif
    }

    //* Removes button listener when object is destroyed.
    private void OnDestroy()
    {
        if (btnExit != null) btnExit.onClick.RemoveListener(ExitGame);
    }
}