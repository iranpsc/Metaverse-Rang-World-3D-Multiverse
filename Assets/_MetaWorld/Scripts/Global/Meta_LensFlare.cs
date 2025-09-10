using UnityEngine;
using UnityEngine.Rendering;

[RequireComponent(typeof(LensFlareCommonSRP))]
public class Meta_LensFlare : MonoBehaviour
{
    private LensFlareComponentSRP lensFlare;
    private Transform cam;
    public LayerMask Mask;

    [Range(0,200)]
    public int RayRange = 100;

    void Start()
    {
        lensFlare = GetComponent<LensFlareComponentSRP>();
        cam = Camera.main.transform;
    }

    void Update()
    {
        //Vector3 dir = (transform.position - cam.position).normalized;
        Vector3 dir = cam.transform.forward;

        Debug.DrawLine(cam.position, dir, Color.red, RayRange);
        if (Physics.Raycast(cam.position, dir, out RaycastHit hit, RayRange, Mask))
        {
            // If the hit object is NOT the light itself, hide flare
            lensFlare.intensity = 0f;
        }
        else
        {
            // Visible → restore intensity
            lensFlare.intensity = 1f;
        }
    }
}