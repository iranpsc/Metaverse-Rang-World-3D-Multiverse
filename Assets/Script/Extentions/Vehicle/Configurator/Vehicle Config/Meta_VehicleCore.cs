using Meta.Vehicle;
using UnityEngine;

namespace Meta
{
    [AddComponentMenu("Meta/Meta_VehicleCore")]
    [HelpURL("https://google.com")]
    public class Meta_VehicleCore : MonoBehaviour
    {
        public enum VehicleType : byte { None, Car, Motorcycle, Bus, Jetpack, Helicopter, Boat, Ship }

        public VehicleType Type;

        private void OnEnable()
        {
            switch(Type)
            {
                case VehicleType.Car:
                    if (!gameObject.GetComponent<Meta_CarSystem>())
                    {
                        gameObject.AddComponent<Meta_CarSystem>();
                    }
                    break;
                case VehicleType.Motorcycle:
                    if (!gameObject.GetComponent<Meta_CarSystem>())
                    {
                        gameObject.AddComponent<Meta_CarSystem>();
                    }
                    break;
                case VehicleType.Bus:
                    if (!gameObject.GetComponent<Meta_CarSystem>())
                    {
                        gameObject.AddComponent<Meta_CarSystem>();
                    }
                    break;
                case VehicleType.Jetpack:
                    if (!gameObject.GetComponent<Meta_CarConfigurator>())
                    {
                        gameObject.AddComponent<Meta_CarConfigurator>();
                    }
                    break;
                case VehicleType.Helicopter:
                    if (!gameObject.GetComponent<Meta_CarConfigurator>())
                    {
                        gameObject.AddComponent<Meta_CarConfigurator>();
                    }
                    break;
                case VehicleType.Boat:
                    if (!gameObject.GetComponent<Meta_CarConfigurator>())
                    {
                        gameObject.AddComponent<Meta_CarConfigurator>();
                    }
                    break;
                case VehicleType.Ship:
                    if (!gameObject.GetComponent<Meta_CarConfigurator>())
                    {
                        gameObject.AddComponent<Meta_CarConfigurator>();
                    }
                    break;
                default:
                    break;
            }

        }
    }
}