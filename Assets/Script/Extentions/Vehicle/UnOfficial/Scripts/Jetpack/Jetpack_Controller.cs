using UnityEngine;

public class Jetpack_Controller : MonoBehaviour
{
    public float moveForce = 15f;
    public float ascendForce = 10f;
    public float descendForce = 5f;
    public float gravityForce = 2f;
    public float rotateSpeed = 60f;

    public Transform[] thrusters;
    public Camera playerCamera;

    private Rigidbody rb;
    private ParticleSystem[] particles;

    public bool HasDriver;
    public bool hidePlayerOnEnter = true;
    private void Start()
    {
        rb = GetComponent<Rigidbody>();
        rb.useGravity = false;
        rb.freezeRotation = true; // lock all rotation axes

        SetupThrusterParticles();
    }

    private void SetupThrusterParticles()
    {
        particles = new ParticleSystem[thrusters.Length];

        for (int i = 0; i < thrusters.Length; i++)
        {
            GameObject psObj = new GameObject("Thruster_Particle");
            psObj.transform.SetParent(thrusters[i]);
            psObj.transform.localPosition = Vector3.zero;
            psObj.transform.localRotation = Quaternion.identity;

            var ps = psObj.AddComponent<ParticleSystem>();
            var main = ps.main;
            main.startLifetime = 0.2f;
            main.startSize = 0.4f;
            main.startColor = new Color(0.3f, 0.3f, 0.3f, 0.25f); // darker gray and transparent
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.loop = true;
            main.playOnAwake = false;

            var emission = ps.emission;
            emission.rateOverTime = 60;

            var shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = 5f;
            shape.radius = 0.05f;

            var vel = ps.velocityOverLifetime;
            vel.enabled = true;
            vel.space = ParticleSystemSimulationSpace.Local;
            vel.x = 0;
            vel.y = -3f;
            vel.z = 0;

            var renderer = ps.GetComponent<ParticleSystemRenderer>();
            renderer.material = new Material(Shader.Find("Particles/Standard Unlit"));
            renderer.material.color = main.startColor.color;
            renderer.renderMode = ParticleSystemRenderMode.Billboard;

            particles[i] = ps;
        }
    }


    private void Update()
    {
        if (HasDriver)
        {
            HandleRotationToCamera();
            HandleThrusterParticles();
        }

    }

    private void FixedUpdate()
    {
        if (HasDriver)
        {
            HandleMovement();
        }
    }

    private void HandleRotationToCamera()
    {
        if (!playerCamera) return;

        Vector3 camForward = playerCamera.transform.forward;
        camForward.y = 0;
        if (camForward.sqrMagnitude > 0.01f)
        {
            Quaternion targetRot = Quaternion.LookRotation(camForward);
            transform.rotation = Quaternion.Slerp(transform.rotation, targetRot, Time.deltaTime * 5f);
        }

        // Lock rotation in X and Z
        Vector3 rot = transform.eulerAngles;
        transform.eulerAngles = new Vector3(0f, rot.y, 0f);
    }

    private void HandleMovement()
    {
        Vector3 input = new Vector3(Input.GetAxis("Horizontal"), 0, Input.GetAxis("Vertical"));
        Vector3 moveDir = transform.TransformDirection(input.normalized);

        // Horizontal movement (WASD)
        rb.AddForce(moveDir * moveForce, ForceMode.Acceleration);

        // Ascend with Space
        if (Input.GetKey(KeyCode.Space))
            rb.AddForce(Vector3.up * ascendForce, ForceMode.Acceleration);
        else
            rb.AddForce(Vector3.down * gravityForce, ForceMode.Acceleration);

        // Descend with Left Shift
        if (Input.GetKey(KeyCode.LeftShift))
            rb.AddForce(Vector3.down * descendForce, ForceMode.Acceleration);

        // Rotate manually with Q and E
        if (Input.GetKey(KeyCode.Q))
            transform.Rotate(0f, -rotateSpeed * Time.deltaTime, 0f);
        if (Input.GetKey(KeyCode.E))
            transform.Rotate(0f, rotateSpeed * Time.deltaTime, 0f);
    }

    private void HandleThrusterParticles()
    {
        bool anyInput = Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.A) ||
                        Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.D) ||
                        Input.GetKey(KeyCode.Space);

        foreach (var p in particles)
        {
            if (anyInput && !p.isPlaying)
                p.Play();
            else if (!anyInput && p.isPlaying)
                p.Stop();
        }

        // Update velocity over lifetime to face opposite of input direction
        Vector3 inputDir = new Vector3(Input.GetAxis("Horizontal"), 0, Input.GetAxis("Vertical")).normalized;

        Vector3 thrustDir = transform.TransformDirection(-inputDir); // mirrored force
        foreach (var ps in particles)
        {
            var vel = ps.velocityOverLifetime;
            vel.enabled = true;
            vel.space = ParticleSystemSimulationSpace.World;
            vel.x = thrustDir.x * 3f;
            vel.y = (Input.GetKey(KeyCode.Space) ? -3f : -1f); // downward smoke
            vel.z = thrustDir.z * 3f;
        }
    }
}
