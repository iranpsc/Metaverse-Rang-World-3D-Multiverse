using UnityEngine;
using UnityEngine.SceneManagement;

namespace Meta
{
    public class Meta_ChangeScene : MonoBehaviour
    {

        public void ChangeScene(string _SceneName)
        {
            SceneManager.LoadScene(_SceneName);
        }
    }
}

