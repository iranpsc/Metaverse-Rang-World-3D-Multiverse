using UnityEngine;

namespace Meta
{
    [AddComponentMenu("Meta/Meta_ObjectSpinner")]
    [HelpURL("https://google.com")]
    public class Meta_ObjectSpinner : MonoBehaviour
    {

        [Header("References")]
        [SerializeField] private Transform ObjectToRotate;

        [Header("Settings")]
        [SerializeField] private float Speed = 100;

        [Header("Debugger")]
        [SerializeField] private bool EnableLog;

        void Start()
        {
            if (ObjectToRotate == null) ObjectToRotate = GetComponent<Transform>();
        }

        void Update()
        {
            ObjectToRotate.Rotate(0, Speed * Time.deltaTime, 0);
        }
    }
}