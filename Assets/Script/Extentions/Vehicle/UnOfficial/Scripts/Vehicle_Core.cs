using UnityEngine;

public enum Vehicle_Type
{
    Car,
    Bus,
    Motorcycle,
    Helicopter,
    Jetpack,
    Boat,
    Ship,
}

public class Vehicle_Core : MonoBehaviour
{
    public Vehicle_Type type;

    public Vehicle_Wheels Wheels;
    public Vehicle_Lights Lights;
    public Vehicle_Seats Seats;
    public Vehicle_Exhaust Exhaust;

    private void Start()
    {
        InitializeVehicle();
        Setup();
    }

    private void InitializeVehicle()
    {
        Wheels = new Vehicle_Wheels(gameObject);
        Wheels.AddCollider();

        Lights = new Vehicle_Lights(gameObject);
        Lights.AutoLight(gameObject);

        Seats = new Vehicle_Seats(gameObject);

        Exhaust = new Vehicle_Exhaust(gameObject);
        Exhaust.AddSmokeToExhausts();
    }

    public void Setup()
    {
        switch (type)
        {
            case Vehicle_Type.Car:
                AddIfMissing<Vehicle_Engine>();
                AddIfMissing<Ground_Vehicle>();
                AddIfMissing<Car_Controller>();
                AddIfMissing<Vehicle_Light_Controller>();
                AddIfMissing<Vehicle_Exhaust_Controller>();
                break;

            case Vehicle_Type.Bus:
                AddIfMissing<Vehicle_Engine>();
                AddIfMissing<Ground_Vehicle>();
                AddIfMissing<Bus_Controller>();
                AddIfMissing<Vehicle_Light_Controller>();
                AddIfMissing<Vehicle_Exhaust_Controller>();
                break;

            case Vehicle_Type.Motorcycle:
                AddIfMissing<Vehicle_Engine>();
                AddIfMissing<Ground_Vehicle>();
                AddIfMissing<Motorcycle_Controller>();
                AddIfMissing<Vehicle_Light_Controller>();
                AddIfMissing<Vehicle_Exhaust_Controller>();
                break;

            case Vehicle_Type.Boat:
                AddIfMissing<Boat_Controller>();
                AddIfMissing<Buoyancy>();
                break;

            case Vehicle_Type.Ship:
                AddIfMissing<Ship_Controller>();
                break;

            case Vehicle_Type.Helicopter:
                AddIfMissing<Helicopter_Controller>();
                break;

            case Vehicle_Type.Jetpack:
                AddIfMissing<Jetpack_Controller>();
                AddIfMissing<Vehicle_Exhaust_Controller>();
                break;
        }
    }

    private void AddIfMissing<T>() where T : Component
    {
        if (!GetComponent<T>())
            gameObject.AddComponent<T>();
    }
}
