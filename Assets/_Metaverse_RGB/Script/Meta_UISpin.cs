using UnityEngine;

[AddComponentMenu("Meta/UI Spin")]
public class Meta_UISpin : MonoBehaviour
{
    [SerializeField] private float Speed = 100f;

    [Tooltip("if is empty it get rect transform from this object else it rotate referenced object")]
    [SerializeField] private RectTransform Element;

    private void Awake()
    {
        if (Element == null) Element = GetComponent<RectTransform>();
    }

    private void Update()
    {
        Element?.Rotate(0, 0, -Speed * Time.deltaTime);
    }
}
