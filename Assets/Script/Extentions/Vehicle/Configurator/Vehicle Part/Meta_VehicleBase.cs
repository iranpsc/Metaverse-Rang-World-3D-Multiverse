using UnityEngine;
using static Meta.Vehicle.Meta_VehicleSeat;
using static Mirror.NetworkRuntimeProfiler;

namespace Meta.Vehicle
{
    public class Meta_VehicleBase : MonoBehaviour
    {
        public bool HasDriver;
        public Transform CurrentDriver;
        public VehicleSeat CurrentSeat;
        public Meta_VehicleSeat Seat;

        public VehicleSeat GetFreeSeat()
        {
            foreach (var seat in Seat.AllSeats)
            {
                if (!seat.Occupied)
                    return seat;
            }

            return default; // empty seat means Occupied=false AND Seat=null
        }

        public void MarkSeatOccupied(Transform seat)
        {
            for (int i = 0; i < Seat.AllSeats.Count; i++)
            {
                if (Seat.AllSeats[i].Seat == seat)
                {
                    var s = Seat.AllSeats[i];
                    s.Occupied = true;
                    Seat.AllSeats[i] = s;
                }
            }
        }

        public void MarkSeatFree(Transform seat)
        {
            for (int i = 0; i < Seat.AllSeats.Count; i++)
            {
                if (Seat.AllSeats[i].Seat == seat)
                {
                    var s = Seat.AllSeats[i];
                    s.Occupied = false;
                    Seat.AllSeats[i] = s;
                }
            }
        }

    }
}
