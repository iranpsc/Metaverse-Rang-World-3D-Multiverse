using System.Collections.Generic;
using UnityEngine;

public class Ground_Vehicle : MonoBehaviour
{
    public Vehicle_Core vehicle;
    public Vehicle_Engine engine;

    //public PhysicsMaterial tireMaterial;
    public TrailRenderer tireTrailPrefab;

    private List<TrailRenderer> tireTrails = new();

    private void Start()
    {
        vehicle = GetComponent<Vehicle_Core>();
        engine = GetComponent<Vehicle_Engine>();
        CreateTireTrails();
    }

    private void FixedUpdate()
    {
        if (vehicle == null || vehicle.Wheels == null) return;

        ApplySteering();
        ApplyMotorTorque();
        UpdateWheelMeshes();
        engine.AdjustCenterOfMassBySpeed();
        UpdateTireTrails();

    }

    private void ApplyMotorTorque()
    {
        float brake = engine.isBraking ? engine.BrakeTorque : 0f;

        // Front Wheel Drive by default
        for (int i = 0; i < vehicle.Wheels.FrontColliders.Count; i++)
        {
            var wc = vehicle.Wheels.FrontColliders[i];
            wc.motorTorque = engine.motorInput * engine.MotorTorque;
            wc.brakeTorque = brake;
        }

        for (int i = 0; i < vehicle.Wheels.RearColliders.Count; i++)
        {
            var wc = vehicle.Wheels.RearColliders[i];
            wc.motorTorque = 0f;
            wc.brakeTorque = brake;
        }
    }

    private void ApplySteering()
    {
        float steerAngle = engine.steerInput * engine.MaxSteerAngle;

        for (int i = 0; i < vehicle.Wheels.FrontColliders.Count; i++)
        {
            var wc = vehicle.Wheels.FrontColliders[i];
            wc.steerAngle = steerAngle;
        }
    }

    private void UpdateWheelMeshes()
    {
        int count = Mathf.Min(vehicle.Wheels.AllWheels.Count, vehicle.Wheels.AllColliders.Count);

        for (int i = 0; i < count; i++)
        {
            GameObject visual = vehicle.Wheels.AllWheels[i];
            WheelCollider wc = vehicle.Wheels.AllColliders[i];

            if (!visual || !wc) continue;

            wc.GetWorldPose(out Vector3 pos, out Quaternion rot);
            visual.transform.position = pos;
            visual.transform.rotation = rot;
        }
    }
    private void CreateTireTrails()
    {
        foreach (var wheel in vehicle.Wheels.AllColliders)
        {
            GameObject trailObj = new GameObject("TireTrail");
            TrailRenderer trail = trailObj.AddComponent<TrailRenderer>();
            trail.material = new Material(Shader.Find("Sprites/Default"));
            trail.time = 0.5f;
            trail.startWidth = 0.1f;
            trail.endWidth = 0.1f;
            trail.emitting = false;

            trail.transform.position = wheel.transform.position;
            trail.transform.SetParent(wheel.transform);
            tireTrails.Add(trail);
        }
    }

    private void UpdateTireTrails()
    {
        for (int i = 0; i < vehicle.Wheels.AllColliders.Count; i++)
        {
            WheelCollider wc = vehicle.Wheels.AllColliders[i];
            TrailRenderer trail = tireTrails[i];

            wc.GetGroundHit(out WheelHit hit);
            float slip = Mathf.Abs(hit.sidewaysSlip);
            trail.emitting = slip > 0.5f;
        }
    }
}
