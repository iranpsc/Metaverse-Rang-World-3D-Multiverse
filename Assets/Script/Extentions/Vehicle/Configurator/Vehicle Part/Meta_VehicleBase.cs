using Mirror;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.InputSystem;
using static Meta.Vehicle.Meta_VehicleSeat; // دسترسی به کلاس VehicleSeat در Meta_VehicleSeat

namespace Meta.Vehicle
{
    // ساختار (Struct) برای نگهداری وضعیت یک صندلی در شبکه
    // IEqualityComparer برای کارکرد صحیح در SyncList ضروری است.
    public struct SeatState : IEqualityComparer<SeatState>
    {
        public int SeatIndex;
        public uint OccupantNetId; // NetId بازیکن نشسته (0 = خالی)
        public bool IsDriver;

        public bool Equals(SeatState x, SeatState y) => x.SeatIndex == y.SeatIndex;
        public int GetHashCode(SeatState _Object) => _Object.SeatIndex.GetHashCode();
    }

    [AddComponentMenu("Meta/Vehicle Base")]
    [HelpURL("https://google.com")]
    // این کلاس باید انتزاعی (abstract) باشد تا Meta_CarSystem بتواند از آن ارث ببرد.
    public abstract class Meta_VehicleBase : NetworkBehaviour
    {
        [Header("Networking & Authority")]
        // NetId راننده. SyncVar این متغیر را بین تمام کلاینت‌ها همگام می‌کند.
        [SyncVar] public uint DriverNetId = 0;

        // SyncList وضعیت تمام صندلی‌ها را در شبکه نگهداری می‌کند.
        public readonly SyncList<SeatState> _SeatState = new SyncList<SeatState>();

        // یک پراپرتی کمکی برای بررسی سریع وجود راننده
        public bool HasDriver => DriverNetId != 0;

        [Header("Vehicle Parts References")]
        public Meta_VehicleSeat Seat;
        public Meta_VehicleWheel Wheel;
        public Meta_VehicleLight Light;
        public Meta_VehicleExhaust Exhaust;

        public abstract void HandleDriverInput();


        public override void OnStartServer()
        {
            base.OnStartServer();
            // این کد فقط روی سرور اجرا می‌شود و SyncList را مقداردهی اولیه می‌کند.
            if (Seat != null)
            {
                for (int i = 0; i < Seat.AllSeats.Count; i++)
                {
                    _SeatState.Add(new SeatState
                    {
                        SeatIndex = i,
                        OccupantNetId = 0, // خالی
                        IsDriver = Seat.AllSeats[i].IsDriverSeat // از Meta_VehicleSeat می‌خواند
                    });
                }
            }
        }

        // =========================================================================
        // Seat Management (Used by Meta_VehicleInteraction.cs)
        // =========================================================================

        // پیدا کردن اولین صندلی خالی
        public (int _Index, Transform _SeatTransform) GetFreeSeat()
        {
            // از SyncList برای پیدا کردن وضعیت اشغال بودن استفاده می‌شود.
            for (int i = 0; i < _SeatState.Count; i++)
            {
                if (_SeatState[i].OccupantNetId == 0) // اگر خالی بود
                {
                    // مرجع Transform را از لیست مرجع محلی (SeatPosition) برمی‌گرداند.
                    Transform _Seat = Seat.AllSeats[i].SeatTransform;
                    return (i, _Seat);
                }
            }
            return (-1, null); // هیچ صندلی خالی پیدا نشد
        }

        // متد [Server] برای اشغال کردن صندلی
        [Server]
        public void MarkSeatOccupied(int _SeatIndex, uint _OccupantNetId)
        {
            if (_SeatIndex < 0 || _SeatIndex >= _SeatState.Count) return;

            // 1. به‌روزرسانی SyncList
            SeatState _State = _SeatState[_SeatIndex];
            _State.OccupantNetId = _OccupantNetId;
            _SeatState[_SeatIndex] = _State; // مهم: باید آیتم را دوباره ست کنید تا SyncList آپدیت شود.

            // 2. تنظیم DriverNetId (اگر صندلی راننده است)
            if (_State.IsDriver)
            {
                DriverNetId = _OccupantNetId; // SyncVar به‌روز می‌شود.
            }
        }

        [Server]
        public void MarkSeatFree(int _SeatIndex)
        {
            SeatState _State = _SeatState[_SeatIndex];

            // ✅ چک حیاتی: اگر صندلی راننده بود و NetId آن با DriverNetId فعلی یکی بود، آن را ریست کن.
            if (_State.IsDriver && DriverNetId == _State.OccupantNetId)
            {
                DriverNetId = 0;
            }
            // توجه: اگر صندلی مسافر باشد، DriverNetId راننده همچنان حفظ می‌شود.

            // پاک کردن OccupantNetId صندلی
            _State.OccupantNetId = 0;
            _SeatState[_SeatIndex] = _State;
        }
        [Server]
        public void ValidateSeat()
        {
            // Safety: ensure seats exist
            if (Seat == null || Seat.AllSeats == null) return;

            for (int i = 0; i < Seat.AllSeats.Count; i++)
            {
                Transform seatTransform = Seat.AllSeats[i].SeatTransform;
                bool hasChild = seatTransform.childCount > 0;

                SeatState state = _SeatState[i];

                if (!hasChild)
                {
                    // Seat is physically empty → mark free (only if not already free)
                    if (state.OccupantNetId != 0)
                    {
                        state.OccupantNetId = 0;

                        // If this was the driver seat, clear driver netId too
                        if (state.IsDriver && DriverNetId == state.OccupantNetId)
                        {
                            DriverNetId = 0;
                        }
                        _SeatState[i] = state;
                    }
                }
                else
                {
                    // Seat has a child → figure out who is sitting there
                    NetworkIdentity occupantNetId = seatTransform.GetChild(0).GetComponent<NetworkIdentity>();
                    if (occupantNetId != null)
                    {
                        uint netId = occupantNetId.netId;

                        // Update seat if mismatched
                        if (state.OccupantNetId != netId)
                        {
                            state.OccupantNetId = netId;

                            if (state.IsDriver)
                                DriverNetId = netId;

                            _SeatState[i] = state;
                        }
                    }
                    else
                    {
                        // Has a child but it's not a networked player = consider seat free
                        if (state.OccupantNetId != 0)
                        {
                            state.OccupantNetId = 0;
                            _SeatState[i] = state;

                            if (state.IsDriver)
                                DriverNetId = 0;
                        }
                    }
                }
            }
        }

        // ...

        public (int _Index, SeatState _SeatData) GetSeatByNetId(uint _NetId)
        {
            for (int i = 0; i < _SeatState.Count; i++)
            {
                if (_SeatState[i].OccupantNetId == _NetId)
                    return (i, _SeatState[i]);
            }
            return (-1, default);
        }

        // متد کمکی برای بررسی اینکه آیا بازیکن با NetId مشخص در هر صندلی نشسته است.
        public bool IsSeatOccupied(uint _NetId)
        {
            return _SeatState.Any(s => s.OccupantNetId == _NetId);
        }
        public bool IsSeatOccupiedByIndex(int seatIndex)
        {
            if (seatIndex < 0 || seatIndex >= _SeatState.Count)
                return false;

            return _SeatState[seatIndex].OccupantNetId != 0;
        }

    }
}