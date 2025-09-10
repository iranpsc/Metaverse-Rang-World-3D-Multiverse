using System.Collections;
using TMPro;
using UnityEngine;

namespace Meta
{
    [HelpURL("GitHub")]
    [AddComponentMenu("Meta/Meta_ErrorPopup")]
    public class Meta_ErrorPopup : MonoBehaviour
    {
        public static Meta_ErrorPopup Instance;

        [SerializeField] private GameObject ErrorPanel;
        [SerializeField] private TMP_Text ErrorText;

        [Header("Debugger")]
        public bool EnableLog;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;

            if (ErrorPanel != null) ErrorPanel.SetActive(false);
        }

        void Start()
        {
            if (EnableLog) Debug.Log("[Meta_ErrorPopup] EDIT");
        }
        public void ShowError(int _ErrorCode)
        {
            string _Message = Meta_ErrorCode.GetError(_ErrorCode);

            if (ErrorText != null) ErrorText.text = _Message;

            if (ErrorPanel != null)
            {
                ErrorPanel.SetActive(true);
                StartCoroutine(AutoClose());
            }
        }
        public void HideError()
        {
            if(ErrorPanel != null) ErrorPanel?.SetActive(false);
        }

        public IEnumerator AutoClose()
        {
            yield return new WaitForSeconds(10f);
            HideError();
        }
    }
}