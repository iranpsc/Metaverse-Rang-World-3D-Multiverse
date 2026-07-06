using System.Collections.Generic;
using UnityEngine;

[System.Serializable]
public class Vehicle_Wheels
{
    public List<GameObject> FrontWheels = new();
    public List<GameObject> RearWheels = new();
    public List<GameObject> AllWheels = new();

    // New: WheelCollider references
    public List<WheelCollider> FrontColliders = new();
    public List<WheelCollider> RearColliders = new();
    public List<WheelCollider> AllColliders = new();

    public Vehicle_Wheels(GameObject vehicle)
    {
        FindWheels(vehicle);
    }

    public void FindWheels(GameObject vehicle)
    {
        if (!vehicle) return;

        Transform[] wheels = vehicle.GetComponentsInChildren<Transform>(true);

        foreach (Transform t in wheels)
        {
            string name = t.name.ToLower();

            if (!name.Contains("wheel") || t.childCount > 0) continue;

            if (name.Contains("front"))
                FrontWheels.Add(t.gameObject);
            else if (name.Contains("rear"))
                RearWheels.Add(t.gameObject);

            AllWheels.Add(t.gameObject);
        }
    }

    public void AddCollider()
    {
        foreach (GameObject wheel in AllWheels)
        {
            if (wheel.GetComponentInChildren<WheelCollider>()) continue;

            MeshFilter meshFilter = wheel.GetComponent<MeshFilter>();
            if (!meshFilter || meshFilter.sharedMesh == null)
            {
                Debug.LogWarning($"[Vehicle_Wheels] Missing mesh on {wheel.name}");
                continue;
            }

            float suspensionDistance = 0.2f;
            float wheelRadius = meshFilter.sharedMesh.bounds.extents.y * wheel.transform.lossyScale.y;

            GameObject colliderObj = new GameObject(wheel.name + "_Collider");
            colliderObj.transform.SetParent(wheel.transform);
            colliderObj.transform.localPosition = new Vector3(0f, suspensionDistance * 0.5f, 0f);
            colliderObj.transform.localRotation = Quaternion.identity;
            colliderObj.transform.SetParent(wheel.transform.parent, true);

            WheelCollider wc = colliderObj.AddComponent<WheelCollider>();
            wc.radius = wheelRadius;
            wc.suspensionDistance = suspensionDistance;
            wc.mass = 30f;

            JointSpring spring = new JointSpring
            {
                spring = 15000f,
                damper = 2500f,
                targetPosition = 0.5f
            };
            wc.suspensionSpring = spring;

            WheelFrictionCurve forwardFriction = wc.forwardFriction;
            forwardFriction.stiffness = 1.5f;
            wc.forwardFriction = forwardFriction;

            WheelFrictionCurve sideFriction = wc.sidewaysFriction;
            sideFriction.stiffness = 2.0f;
            wc.sidewaysFriction = sideFriction;

            // Add to correct list
            if (FrontWheels.Contains(wheel))
                FrontColliders.Add(wc);
            else if (RearWheels.Contains(wheel))
                RearColliders.Add(wc);

            AllColliders.Add(wc);
        }
    }
}
