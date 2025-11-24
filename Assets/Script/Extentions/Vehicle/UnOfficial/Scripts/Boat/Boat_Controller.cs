using UnityEngine;

public class Boat_Controller : MonoBehaviour
{
    /* CUSTOM SCRIPT*/

    public Vehicle_Core vehicle;
    public bool HasDriver;
    public bool hidePlayerOnEnter = true;
    /* CUSTOM SCRIPT*/

    [Header("Boat Movement")]
    public float maxForwardSpeed = 5f;
    public float maxBackwardSpeed = 2f;
    public float acceleration = 3f;
    public float turnSpeed = 30f; // degrees per second

    [Header("References")]
    public Rigidbody rb;
    public Transform pedal;

    public Transform propeller;
    public Transform steeringWheel;

    [Header("Animation Settings")]
    public float pedalRotationSpeed = 360f; // degrees per second
    public float propellerRotationSpeed = 1000f;
    public float maxSteeringWheelAngle = 22f;

    // Inputs
    float pedalInput;  // -1 to 1, backward/forward
    float steeringInput; // -1 left, 1 right

    // Internal state
    float currentSpeed = 0f;
    public bool onLand = false;  // Whether the boat is on land
    /* CUSTOM SCRIPT*/
    private void Start()
    {
        vehicle = GetComponent<Vehicle_Core>();
    }

    void Update()
    {
        if (!HasDriver) return;
        /* CUSTOM SCRIPT*/
        // Get input - pedals control forward/backward
        pedalInput = Input.GetAxis("Vertical");

        // Steering input - left/right arrows or A/D keys
        steeringInput = Input.GetAxis("Horizontal");

        // Check if the boat is on land
        CheckGround();

        AnimatePropellerAndPedals();
        AnimateSteeringWheel();
    }

    void FixedUpdate()
    {
        if (!HasDriver) return;
        if (!onLand)  // Only move if the boat is not on land
        {
            HandleMovement();
            HandleTurning();
        }
    }

    void HandleMovement()
    {
        // Accelerate/decelerate toward target speed based on pedal input
        float targetSpeed = 0f;
        if (pedalInput > 0)
            targetSpeed = maxForwardSpeed * pedalInput;
        else
            targetSpeed = maxBackwardSpeed * pedalInput; // negative speed

        currentSpeed = Mathf.MoveTowards(currentSpeed, targetSpeed, acceleration * Time.fixedDeltaTime);

        // Move the boat forward based on current speed
        Vector3 velocity = transform.forward * currentSpeed;
        rb.linearVelocity = new Vector3(velocity.x, rb.linearVelocity.y, velocity.z); // preserve vertical velocity for physics
    }

    void HandleTurning()
    {
        // Boat turns based on steering input and current speed magnitude
        float speedFactor = Mathf.Clamp01(Mathf.Abs(currentSpeed) / maxForwardSpeed);

        // Reverse turn direction if going backward
        float directionMultiplier = (currentSpeed >= 0) ? 1f : -1f;

        float turnAmount = steeringInput * turnSpeed * speedFactor * directionMultiplier * Time.fixedDeltaTime;

        Quaternion turnRotation = Quaternion.Euler(0f, turnAmount, 0f);
        rb.MoveRotation(rb.rotation * turnRotation);
    }

    void AnimatePropellerAndPedals()
    {
        if (propeller)
        {
            // Determine rotation speed based on the current speed of the boat
            propellerRotationSpeed = Mathf.Abs(currentSpeed) * 500f / maxForwardSpeed; // Adjust speed scaling

            // Reverse the propeller rotation if moving backward (negative input)
            if (pedalInput < 0)
            {
                propellerRotationSpeed = -propellerRotationSpeed;
            }

            // Rotate the propeller and pedal around the X-axis
            propeller.Rotate(Vector3.right * propellerRotationSpeed * Time.deltaTime, Space.Self);
            pedal.Rotate(Vector3.right * propellerRotationSpeed * Time.deltaTime, Space.Self);
        }
    }

    void AnimateSteeringWheel()
    {
        if (steeringWheel)
        {
            // Steering wheel rotates left/right based on input
            float targetAngle = steeringInput * maxSteeringWheelAngle;
            Vector3 currentEuler = steeringWheel.localEulerAngles;
            if (currentEuler.y > 180) currentEuler.y -= 360;
            float newY = Mathf.Lerp(currentEuler.y, -targetAngle, 5f * Time.deltaTime);
            steeringWheel.localEulerAngles = new Vector3(currentEuler.x, newY, currentEuler.z);
        }
    }

    void CheckGround()
    {
        // Raycast from the boat downward
        RaycastHit hit;
        if (Physics.Raycast(transform.position, Vector3.down, out hit, .5f))
        {
            if (hit.collider.CompareTag("Ground")) // Check if hit object has the "Ground" tag
            {
                onLand = true; // Stop movement if the boat is on land
            }
        }
        else
        {
            onLand = false; // Re-enable movement when not on land
        }
    }
}
