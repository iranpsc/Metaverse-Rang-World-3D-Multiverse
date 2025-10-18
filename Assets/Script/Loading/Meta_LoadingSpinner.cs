using UnityEngine;

namespace Meta
{
    [AddComponentMenu("Meta/Meta LoadingSpinner")]
    [HelpURL("https://google.com")]
    public class Meta_LoadingSpinner : MonoBehaviour
    {

        [Header("References")]
        [SerializeField] private RectTransform Element;

        [Header("Settings")]
        [SerializeField] private float Speed;
        
        [Header("Debugger")]
        [SerializeField] private bool EnableLog;

        void Start()
        {
            if (Element == null) Element = GetComponent<RectTransform>();
        }

        void Update()
        {
            Element?.Rotate(0, 0, Speed * Time.deltaTime);
        }
    }
}