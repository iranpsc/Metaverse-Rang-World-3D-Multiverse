using System;
using System.Diagnostics;
using UnityEngine;

public class ExitGameButton : MonoBehaviour
{
        [SerializeField] private bool forceKillWindowsBuild = true;

        public void ExitGame()
        {
                UnityEngine.Debug.Log("[ExitGameButton] ExitGame clicked.");

#if UNITY_EDITOR
                UnityEngine.Debug.Log("[ExitGameButton] Editor mode detected. Stopping play mode is handled by the editor.");
                UnityEditor.EditorApplication.isPlaying = false;
                return;
#else
        UnityEngine.Debug.Log("[ExitGameButton] Application.Quit called.");
        Application.Quit();

#if UNITY_STANDALONE_WIN
        if (forceKillWindowsBuild)
        {
            UnityEngine.Debug.Log("[ExitGameButton] Windows hard process kill requested.");

            try
            {
                Process.GetCurrentProcess().Kill();
            }
            catch (Exception killError)
            {
                UnityEngine.Debug.LogError("[ExitGameButton] Process.Kill failed: " + killError.Message);
            }

            try
            {
                Environment.Exit(0);
            }
            catch (Exception exitError)
            {
                UnityEngine.Debug.LogError("[ExitGameButton] Environment.Exit failed: " + exitError.Message);
            }
        }
#endif
#endif
        }
}