using TMPro;
using UnityEngine;

namespace Meta
{
    [HelpURL("GitHub")]
    [AddComponentMenu("Meta/Meta PlayerName")]
    public class Meta_PlayerName : MonoBehaviour
    {
        private Meta_UserGlobalData Data;
        private TMP_Text PlayerName;

        [Header("Debugger")]
        public bool EnableLog;

        private void Awake()
        {
            Data = Meta_UserGlobalData.Instance;
        }
        void Start()
        {
            if (EnableLog) Debug.Log("[Meta_PlayerName] EDIT");
            PlayerName = GetComponent<TMP_Text>();
            PlayerName.text = Data.Username;
        }
    }
}