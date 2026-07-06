using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Meta
{
    [AddComponentMenu("Meta/Meta SceneManager")]
    [HelpURL("https://google.com")]
    public class Meta_SceneManager : MonoBehaviour
    {
        [SerializeField] private AutoSceneChange AutoChange;

        [Header("Debugger")]
        [SerializeField] private bool EnableLog;

        private void Start()
        {
            if (AutoChange.Auto && !string.IsNullOrEmpty(AutoChange.NewScene)) StartCoroutine(ChangeWithDelay(AutoChange.NewScene));
        }
        public void Change(string _Name)
        {
            SceneManager.LoadScene(_Name);
        }

        public void DelayChange(string _Name)
        {
            StartCoroutine(ChangeWithDelay(_Name));
        }

        private IEnumerator ChangeWithDelay(string _Name)
        {
            yield return new WaitForSeconds(AutoChange.Delay);

            Change(_Name);
        }
    }
    [System.Serializable]
    public class AutoSceneChange
    {
        public bool Auto;
        public float Delay;
        public string NewScene;
    }
}