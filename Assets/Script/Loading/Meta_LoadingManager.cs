using RTLTMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Meta
{
    [AddComponentMenu("Meta/Meta_LoadingManager")]
    [HelpURL("https://google.com")]
    public class Meta_LoadingManager : MonoBehaviour
    {

        [Header("References")]
        [SerializeField] private Image Background;
        [SerializeField] private RTLTextMeshPro Tips;

        [Header("Settings")]
        [SerializeField] private Sprite[] LoadingImages;
        [SerializeField] private string[] LoadingTips;

        [Header("Debugger")]
        [SerializeField] private bool EnableLog;

        void Start()
        {
            if (LoadingImages != null)
            {
                Background.sprite = LoadingImages?[Random.Range(0, LoadingImages.Length)];
            }
            if (Tips != null)
            {
                Tips.text = LoadingTips?[Random.Range(0, LoadingTips.Length)];
            }
        }
    }
}