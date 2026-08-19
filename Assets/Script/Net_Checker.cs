using Network_A.Bootstrap;
using UnityEngine;
using UnityEngine.SceneManagement;

public class Net_Checker : MonoBehaviour
{
    [SerializeField] private string LoginScene = "Base.Login";
    [SerializeField] private string OfflineScene = "Base.Offline";

    private bool SceneLoaded;


    private void OnEnable()
    {
        StartupNetworkSceneRouter.OnNetworkStateChanged += OnNetworkStateChanged;
    }

    private void OnDisable()
    {
        StartupNetworkSceneRouter.OnNetworkStateChanged -= OnNetworkStateChanged;
    }

    private void Start()
    {
        OnNetworkStateChanged(StartupNetworkSceneRouter.CurrentState);
    }

    private void OnNetworkStateChanged(StartupNetworkSceneRouter.NetworkState _State)
    {
        if (SceneLoaded)
            return;

        switch(_State)
        {
            case StartupNetworkSceneRouter.NetworkState.Online:
                SceneLoaded = true;
                SceneManager.LoadScene(LoginScene);
                break;
            case StartupNetworkSceneRouter.NetworkState.InternetUnavailable:
            case StartupNetworkSceneRouter.NetworkState.ServerUnavailable:
                SceneLoaded = true;
                SceneManager.LoadScene(OfflineScene);
                break;

            // Unknown / Checking -> wait
        }
    }
}
