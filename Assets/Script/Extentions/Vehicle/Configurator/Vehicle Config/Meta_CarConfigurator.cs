using Meta.Vehicle;
using UnityEngine;

namespace Meta
{
    [AddComponentMenu("Meta/Meta_CarConfigurator")]
    [HelpURL("https://google.com")]
    public class Meta_CarConfigurator : MonoBehaviour
    {
        public Meta_VehiclePart Body;
        public Meta_VehicleSeat Seat;
        public Meta_VehicleWheel Wheel;
        public Meta_VehicleLight Light;
        public Meta_VehicleExhaust Exhaust;

        public void OnEnable()
        {
            GetData();
        }
        private void GetData()
        {
            Body = new Meta_VehiclePart(gameObject);
            Seat.GetSeats(Body.Parts);
            Wheel.GetWheels(Body.Parts);
            Light.GetLight(Body.Parts);
            Exhaust.GetExhausts(Body.Parts);
        }


    }
}