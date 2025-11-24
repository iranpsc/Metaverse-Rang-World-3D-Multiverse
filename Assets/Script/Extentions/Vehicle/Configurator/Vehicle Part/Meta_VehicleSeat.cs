using System;
using System.Collections.Generic;
using UnityEngine;
using static Mirror.NetworkRuntimeProfiler;

namespace Meta.Vehicle
{
    [AddComponentMenu("Meta/Meta_VehicleSeat")]
    [HelpURL("https://google.com")]
    [Serializable]
    public class Meta_VehicleSeat
    {
        public List<VehicleSeat> AllSeats;
        public List<VehicleSeat> DriverSeats;
        public List<VehicleSeat> PassengerSeats;
        [Serializable]
        public class VehicleSeat
        {
            public Transform Seat;
            public bool Occupied;
            public VehicleSeat(Transform _Seat, bool _Occupied)
            {
                Seat = _Seat;
                Occupied = _Occupied;
            }
        }
        public virtual void GetSeats(Transform[] _Part)
        {
            foreach (Transform _Seats in _Part)
            {
                string _name = _Seats.name.ToLower();
                if (!_name.Contains("seat") || _Seats.childCount > 0 /*|| !_Seats.GetComponent<MeshRenderer>()*/) continue;

                AllSeats.Add(new VehicleSeat(_Seats, false));

                if (_name.Contains("driver"))
                    DriverSeats.Add(new VehicleSeat(_Seats, false));

                if (_name.Contains("passenger"))
                    PassengerSeats.Add(new VehicleSeat(_Seats, false));
            }
        }
    }
}