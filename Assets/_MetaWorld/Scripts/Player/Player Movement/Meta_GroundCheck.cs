using UnityEngine;

namespace Meta
{
    [HelpURL("GitHub")]
    [AddComponentMenu("Meta/Meta_GroundCheck")]
    public class Meta_GroundCheck : MonoBehaviour
    {
        [Header("Debugger")]
        public bool EnableLog;

        void Start()
        {
            if (EnableLog) Debug.Log("[Meta_GroundCheck] EDIT");
        }

        void Update()
        {

        }
    }
}