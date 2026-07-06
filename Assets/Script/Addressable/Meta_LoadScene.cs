using UnityEngine;

namespace Meta
{
    [AddComponentMenu("Meta/Meta_LoadScene")]
    [HelpURL("https://github.com/DreamFaver")]
    public class Meta_LoadScene : MonoBehaviour
    {
        public void LoadNewScene(string _SceneAddress)
        {
            Meta_SceneFlowManager.Instance?.LoadScene(_SceneAddress);
        }
    }
}