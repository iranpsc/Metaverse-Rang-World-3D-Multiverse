using UnityEngine;

public class Vehicle_Engine : MonoBehaviour
{
    [Header("Drive Settings")]
    public float MotorTorque = 1500f;
    public float BrakeTorque = 3000f;
    public float MaxSteerAngle = 30f;

    public float steerInput;
    public float motorInput;
    public bool isBraking;

    public Vector3 baseCenterOfMass = new Vector3(0f, -0.3f, 0f); 
    public float comLoweringFactor = 0.2f; // How much lower CoM goes at high speed
    public float maxSpeedForCoM = 100f; // km/h or m/s depending on your units
    public Rigidbody rb;

    public void Start()
    {
        rb = GetComponent<Rigidbody>();
    }

    public void AdjustCenterOfMassBySpeed()
    {
        if (rb == null) return;

        float speed = rb.linearVelocity.magnitude; // in m/s
        float t = Mathf.Clamp01(speed / maxSpeedForCoM); // 0 to 1

        // Interpolate Y offset from base value to lowered value
        float loweredY = baseCenterOfMass.y - comLoweringFactor;
        float currentY = Mathf.Lerp(baseCenterOfMass.y, loweredY, t);

        rb.centerOfMass = new Vector3(baseCenterOfMass.x, currentY, baseCenterOfMass.z);
    }
}
