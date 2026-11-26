using UnityEngine;

public class Motorcycle_Controller : MonoBehaviour
{
    public Vehicle_Core vehicle;
    public Vehicle_Engine engine;
    public bool HasDriver;
    public bool hidePlayerOnEnter = true;
    private void Start()
    {
        vehicle = GetComponent<Vehicle_Core>();
        engine = GetComponent<Vehicle_Engine>();
    }
    private void Update()
    {
        if (HasDriver)
        {
            engine.steerInput = Input.GetAxis("Horizontal");
            engine.motorInput = Input.GetAxis("Vertical");
            engine.isBraking = Input.GetKey(KeyCode.Space);

            vehicle.Exhaust?.EnableSmoke(true);
            vehicle.Lights?.SetLightsIntensity(true);
        }
        else
        {
            vehicle.Exhaust?.EnableSmoke(false);
            vehicle.Lights?.SetLightsIntensity(false);
        }
    }
}
