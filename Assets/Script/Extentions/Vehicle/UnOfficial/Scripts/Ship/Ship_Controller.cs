using UnityEngine;

public class Ship_Controller : MonoBehaviour
{
    public Transform island;             // The island to rotate around
    public float orbitRadius = 200f;      // Distance from island
    public float maxOrbitSpeed = 1f;    // Max degrees per second
    public float accelerationTime = 3f;  // Time to reach max speed

    public float maxFuel = 36000f;       // Fuel units (e.g. 1000 units/hour * 36 hours)
    public float fuelBurnRate = 1000f;   // Fuel units burnt per hour

    [SerializeField] private float currentFuel;
    [SerializeField] private float currentSpeed = 0f;     // Current orbit speed in degrees/sec
    private float targetSpeed = 0f;

    private float accelerationRate;

    private Vector3 orbitAxis = Vector3.up; // Assuming Y axis up

    public Transform Propeller_Left; // left PROPELLER
    public Transform Propeller_Right; // right PROPELLER
    public Transform Propeller_Back; // middle PROPELLER
    private float propellerRotationSpeed;
    private void Start()
    {
        currentFuel = maxFuel;

        accelerationRate = maxOrbitSpeed / accelerationTime;

        // Position ship at orbitRadius distance from island
        Vector3 direction = (transform.position - island.position).normalized;
        transform.position = island.position + direction * orbitRadius;

        targetSpeed = maxOrbitSpeed; // Start moving

        // detecting the ship 3 propellers
        Propeller_Left = transform.FindDeepChild("Propeller_Left");
        Propeller_Right = transform.FindDeepChild("Propeller_Right");
        Propeller_Back = transform.FindDeepChild("Propeller_Back");
        if (!Propeller_Left || !Propeller_Right || !Propeller_Back)
            Debug.LogWarning ("one or more properllers are missing!");
    }

    private void Update()
    {
        if (island == null) return;

        float deltaTime = Time.deltaTime;

        // Burn fuel based on burn rate (units per hour)
        float fuelConsumed = (fuelBurnRate / 3600f) * deltaTime; // fuel per frame
        currentFuel -= fuelConsumed;
        currentFuel = Mathf.Max(currentFuel, 0f);

        // Adjust speed based on fuel
        if (currentFuel > 0)
        {
            // Accelerate smoothly to max speed
            targetSpeed = maxOrbitSpeed;
            currentSpeed = Mathf.MoveTowards(currentSpeed, targetSpeed, accelerationRate * deltaTime);
        }
        else
        {
            // Decelerate smoothly to stop
            targetSpeed = 0f;
            currentSpeed = Mathf.MoveTowards(currentSpeed, targetSpeed, accelerationRate * deltaTime);
        }

        // Rotate the ship around the island smoothly
        // Orbit angle per frame = currentSpeed * deltaTime
        transform.RotateAround(island.position, orbitAxis, currentSpeed * deltaTime);

        // Maintain orbit radius (in case of drifting)
        Vector3 offset = transform.position - island.position;
        offset = offset.normalized * orbitRadius;
        transform.position = island.position + offset;

        // Optional: rotate ship to face direction of movement (tangent to orbit)
        Vector3 tangentDir = Vector3.Cross(orbitAxis, offset).normalized;
        transform.rotation = Quaternion.LookRotation(tangentDir, orbitAxis);

        RotatePropellers();
    }


    private void RotatePropellers()
    {
        if (currentFuel > 0)
        {
            // Determine rotation speed based on the current speed of the boat
            propellerRotationSpeed = Mathf.Abs(maxOrbitSpeed) * 500f / maxOrbitSpeed; // Adjust speed scaling

            // Rotate the propeller and pedal around the Z-axis
            Propeller_Back.Rotate(Vector3.forward * propellerRotationSpeed * Time.deltaTime, Space.Self);
            Propeller_Left.Rotate(Vector3.forward * -propellerRotationSpeed * Time.deltaTime, Space.Self);
            Propeller_Right.Rotate(Vector3.forward * propellerRotationSpeed * Time.deltaTime, Space.Self);

        }
    }
}
