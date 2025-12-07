using UnityEngine;
using UnityEngine.InputSystem;

[RequireComponent(typeof(Rigidbody))]
public class Helicopter_Controller : MonoBehaviour
{
    #region Old
    
    public InputActionReference Move;
    public InputActionReference Up;
    public InputActionReference Down;
    [Header("Helicopter Parts")]
    public Transform mainRotor;
    public Transform tailRotor;
    public Transform centerOfMass;

    [Header("Flight Settings")]
    public float maxEnginePower = 1000f;
    public float liftForce = 8f;
    public float yawPower = 100f;
    float currentYaw = 0f;

    public float moveForce = 10f;
    public float tiltAngle = 15f;
    public float tiltSpeed = 2f;
    public float engineAcceleration = 2f;
    public float engineDeacceleration = 1f;

    [Header("Rotor Rotation Speed")]
    public float rotorSpinSpeed = 1000f;

    [Header("Altitude Settings")]
    public float maxAltitude = 100f;
    public LayerMask groundMask;

    [Header("Pilot Control")]
    public bool HasDriver = false;
    public bool hidePlayerOnEnter = true;

    private Rigidbody rb;
    private float enginePower = 0f;
    private float targetEnginePower = 0f;
    private void OnEnable()
    {
        Move.action.Enable();
        Up.action.Enable();
        Down.action.Enable();
    }
    private void OnDisable()
    {
        Move.action.Disable();
        Up.action.Disable();
        Down.action.Disable();
    }
    private void Start()
    {
        rb = GetComponent<Rigidbody>();
        rb.mass = 800; // Set your mass here
        rb.centerOfMass = transform.InverseTransformPoint(centerOfMass.position);

        Transform[] part = gameObject.GetComponentsInChildren<Transform>(true);

        foreach (Transform t in part)
        {
            string name = t.name.ToLower();
            if (name.Contains("seat") && t.childCount > 0)
                centerOfMass = t;
            if (!name.Contains("rotor") || t.childCount > 0) continue;

            if (name.Contains("front"))
                mainRotor = t;
            else if (name.Contains("rear"))
                tailRotor = t;

        }

    }

    void Update()
    {
        if (HasDriver)
        {
            HandleRotorVisuals();
            HandleInputs();
        }
        else
            targetEnginePower = 0f;
    }

    void FixedUpdate()
    {
        if (HasDriver)
        {
            SmoothEnginePower();
            ApplyLift();
            ApplyMovement();
            ApplyRotation();
            AutoLevelTilt();
            LimitAltitude();
        }
    }

    void HandleInputs()
    {
        if (Up.action.IsPressed())
        {
            targetEnginePower = maxEnginePower;
        }
        else if (Down.action.IsPressed())
        {
            targetEnginePower = 0f;
        }
        else
        {
            targetEnginePower = maxEnginePower * 0.6f;
        }
    }

    void SmoothEnginePower()
    {
        if (enginePower < targetEnginePower)
            enginePower += Time.deltaTime * engineAcceleration * 100f;
        else
            enginePower -= Time.deltaTime * engineDeacceleration * 100f;

        enginePower = Mathf.Clamp(enginePower, 0f, maxEnginePower);
    }

    void ApplyLift()
    {
        if (GetAltitude() < maxAltitude)
        {
            Vector3 upForce = transform.up * (enginePower * liftForce);
            rb.AddForce(upForce);
        }
    }

    void ApplyMovement()
    {
        float h = Move.action.ReadValue<Vector2>().x;
        float v = Move.action.ReadValue<Vector2>().y;

        Vector3 moveDir = transform.forward * v + transform.right * h;
        rb.AddForce(moveDir * moveForce);

        // Visual tilt
        Quaternion targetTilt = Quaternion.Euler(
            v * -tiltAngle,
            transform.eulerAngles.y,
            h * -tiltAngle);
        transform.rotation = Quaternion.Slerp(transform.rotation, targetTilt, Time.deltaTime * tiltSpeed);
    }

    void ApplyRotation()
    {
        float yawInput = 0f;
        if (Input.GetKey(KeyCode.Q)) yawInput = -1f;
        if (Input.GetKey(KeyCode.E)) yawInput = 1f;

        if (yawInput == 0)
            currentYaw = Mathf.Lerp(currentYaw, 0f, Time.deltaTime * 3f);
        // Smoothly change yaw over time
        float yawSpeed = yawPower; // degrees per second
        currentYaw = Mathf.Lerp(currentYaw, yawInput * yawSpeed, Time.deltaTime * 5f);

        if (Mathf.Abs(currentYaw) > 0.1f)
        {
            transform.Rotate(Vector3.up, currentYaw * Time.deltaTime, Space.World);
        }

    }


    void HandleRotorVisuals()
    {
        float spinMultiplier = (enginePower / maxEnginePower);

        if (mainRotor != null)
            mainRotor.Rotate(Vector3.up, rotorSpinSpeed * Time.deltaTime * spinMultiplier);
        if (tailRotor != null)
            tailRotor.Rotate(Vector3.right, rotorSpinSpeed * Time.deltaTime * spinMultiplier);
    }

    void AutoLevelTilt()
    {
        if (!Input.anyKey && HasDriver)
        {
            Quaternion levelRot = Quaternion.Euler(0f, transform.eulerAngles.y, 0f);
            transform.rotation = Quaternion.Slerp(transform.rotation, levelRot, Time.deltaTime * 1.5f);
        }
    }

    void LimitAltitude()
    {
        float currentAlt = GetAltitude();
        if (currentAlt >= maxAltitude && enginePower > 0)
        {
            rb.linearVelocity = new Vector3(rb.linearVelocity.x, Mathf.Min(rb.linearVelocity.y, 0f), rb.linearVelocity.z);
        }
    }

    float GetAltitude()
    {
        Ray ray = new Ray(transform.position, -Vector3.up);
        if (Physics.Raycast(ray, out RaycastHit hit, 1000f, groundMask))
        {
            return hit.distance;
        }
        return 0f;
    }

    // Call this when pilot enters/exits
    public void SetPilot(bool state)
    {
        HasDriver = state;
    }
    
    #endregion
    public enum Axis
    {
        X,
        Y,
        Z
    }
    public Axis RotationAxis;
    public float BladeSpeed;
    public bool InverseRotation;
    private Vector3 Rotation;
    private float RotateDegres;

    //private void Start()
    //{
    //    Rotation = transform.localEulerAngles;
    //}

    //private void Update()
    //{
    //    if(InverseRotation)
    //    {
    //        RotateDegres -= BladeSpeed * Time.deltaTime;
    //    }
    //    else
    //    {
    //        RotateDegres += BladeSpeed * Time.deltaTime;
    //    }
    //    RotateDegres = RotateDegres % 360;
    //    switch(RotationAxis)
    //    {
    //        case Axis.Y:
    //            transform.localRotation = Quaternion.Euler(Rotation.x, RotateDegres, Rotation.z);
    //            break;
    //        case Axis.Z:
    //            transform.localRotation = Quaternion.Euler(Rotation.x, Rotation.y, RotateDegres);
    //            break;
    //        default:
    //            transform.localRotation = Quaternion.Euler(RotateDegres, Rotation.y, Rotation.z);
    //            break;
    //    }
    //}
}