using Mirror;
using UnityEngine;

namespace Meta.Vehicle
{
    [AddComponentMenu("Meta/Vehicle Configurator")]
    [HelpURL("https://github.com/DreamFaver")]
    public class Meta_VehicleConfigurator : NetworkBehaviour
    {

        [SerializeField] private Meta_VehiclePart _Body;
        [SerializeField] private Meta_VehicleSeat _Seat = new Meta_VehicleSeat();
        [SerializeField] private Meta_VehicleWheel _Wheel = new Meta_VehicleWheel();
        [SerializeField] private Meta_VehicleLight _Light = new Meta_VehicleLight();
        [SerializeField] private Meta_VehicleExhaust _Exhaust = new Meta_VehicleExhaust();

        public Meta_VehiclePart Body => _Body;
        public Meta_VehicleSeat Seat => _Seat;
        public Meta_VehicleWheel Wheel => _Wheel;
        public Meta_VehicleLight Light => _Light;
        public Meta_VehicleExhaust Exhaust => _Exhaust;

        private Meta_VehicleBase _VehicleBase;

        public void Awake()
        {
            GetData();

            _VehicleBase = GetComponent<Meta_VehicleBase>();
            if (_VehicleBase != null )
            {
                _VehicleBase.Seat = _Seat;
                _VehicleBase.Wheel = _Wheel;
                _VehicleBase.Light = _Light;
                _VehicleBase.Exhaust = _Exhaust;

                _Wheel.SetWheels();
                _Light.SetLight();
                _Exhaust.SetExhaust();
            }
        }

        private void GetData()
        {
            _Body = new Meta_VehiclePart(gameObject);

            Transform[] _Parts = _Body.AllParts;

            _Seat.GetSeats(_Parts);
            _Wheel.GetWheelsAndSteering(_Parts);
            _Light.GetLight(_Parts);
            _Exhaust.GetExhaustsAndPropellers(_Parts);
        }
    }
}