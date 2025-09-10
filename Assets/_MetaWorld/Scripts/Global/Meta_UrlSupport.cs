using UnityEngine;

namespace Meta
{
    public class Meta_UrlSupport : MonoBehaviour
    {

        [Header("Debugger")]
        public bool EnableLog;

        public void OpenURL(string _Url) => Application.OpenURL(_Url);
    }
}