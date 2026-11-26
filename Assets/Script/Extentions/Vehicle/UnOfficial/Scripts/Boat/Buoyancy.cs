using UnityEngine;
using System.Collections.Generic;

public class Buoyancy : MonoBehaviour
{
    [Header("Buoyancy Settings")]
    public float waterLevel = 0f;
    public float buoyancyForce = 50f;
    public float dampingFactor = 0.7f;
    public float offsetY = 0.1f;

    private Rigidbody rb;
    private List<Transform> buoyancyPoints = new List<Transform>();

    void Start()
    {
        rb = GetComponent<Rigidbody>();

        if (rb == null)
        {
            Debug.LogError("No Rigidbody found on the object!");
            return;
        }

        CreateBuoyancyPoints();
    }

    void FixedUpdate()
    {
        ApplyBuoyancy();
    }

    void CreateBuoyancyPoints()
    {
        Renderer renderer = GetComponent<Renderer>();
        if (renderer == null)
        {
            Debug.LogError("No Renderer found! Make sure the object has a Renderer.");
            return;
        }

        Bounds bounds = renderer.bounds;

        Vector3[] cornerPositions = new Vector3[]
        {
            new Vector3(bounds.min.x, bounds.min.y + offsetY, bounds.min.z), // Bottom-Left (Back)
            new Vector3(bounds.max.x, bounds.min.y + offsetY, bounds.min.z), // Bottom-Right (Back)
            new Vector3(bounds.min.x, bounds.min.y + offsetY, bounds.max.z), // Bottom-Left (Front)
            new Vector3(bounds.max.x, bounds.min.y + offsetY, bounds.max.z)  // Bottom-Right (Front)
        };

        for (int i = 0; i < cornerPositions.Length; i++)
        {
            GameObject point = new GameObject($"BuoyancyPoint_{i + 1}");
            point.transform.position = cornerPositions[i];
            point.transform.parent = transform;
            buoyancyPoints.Add(point.transform);
        }
    }

    void ApplyBuoyancy()
    {
        foreach (Transform point in buoyancyPoints)
        {
            Vector3 pointWorldPosition = point.position;
            float submergedDepth = waterLevel - pointWorldPosition.y;

            if (submergedDepth > 0)
            {
                Vector3 buoyancy = Vector3.up * buoyancyForce * submergedDepth;
                rb.AddForceAtPosition(buoyancy, pointWorldPosition);

                // Optional: damping based on velocity
                rb.AddForce(-rb.linearVelocity * dampingFactor, ForceMode.Acceleration);
            }
        }
    }
}
