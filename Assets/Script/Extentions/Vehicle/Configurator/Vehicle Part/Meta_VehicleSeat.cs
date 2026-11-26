using System;
using System.Collections.Generic;
using UnityEngine;
using static Meta.Vehicle.Meta_VehiclePart;

namespace Meta.Vehicle
{
    [AddComponentMenu("Meta/Vehicle Seat")]
    [HelpURL("https://google.com")]
    [Serializable]
    public class Meta_VehicleSeat
    {
        public List<VehicleSeat> AllSeats = new List<VehicleSeat>();
        public List<VehicleSeat> DriverSeats = new List<VehicleSeat>();
        public List<VehicleSeat> PassengerSeats = new List<VehicleSeat>();
        
        [Serializable]
        public class VehicleSeat
        {
            public Transform SeatTransform;
            public bool IsDriverSeat;
            public VehicleSeat(Transform _Seat, bool _IsDriver)
            {
                SeatTransform = _Seat;
                IsDriverSeat = _IsDriver;
            }
        }
        public virtual void GetSeats(Transform[] _Parts)
        {
            AllSeats.Clear();
            DriverSeats.Clear();
            PassengerSeats.Clear();

            foreach (Transform _Part in _Parts)
            {
                string _Name = _Part.name.ToLower();

                if (!_Name.Contains("seat")) continue;

                if (!IsVisualOrAttachmentPart(_Part)) continue;

                bool _IsDriver = _Name.Contains("driver");
                VehicleSeat _NewSeat = new VehicleSeat(_Part, _IsDriver);

                AllSeats.Add(_NewSeat);

                if (_IsDriver)
                    DriverSeats.Add(_NewSeat);
                else
                    PassengerSeats.Add(_NewSeat);
            }
        }
    }
}