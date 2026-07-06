using Meta.Player.Core;
using Meta.Vehicle;
using Mirror;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Meta
{
    [AddComponentMenu("Meta/Vehicle Authority")]
    [HelpURL("https://github.com/DreamFaver")]
    public class Meta_VehicleAuthority : NetworkBehaviour
    {
        public GameObject Trigger;
        public Meta_VehicleBase Vehicle;
        public Vector3 SeatOffset;
        public GameObject PreviousPlayer;
        public bool CanEnter;
        public InputActionReference Interact;
        public bool Intracted;
        public float ExitDistance = 3f;
        [ReadOnly] public int SeatID = -1;

        public bool Entered;

        public void OnEnable()
        {
            Vehicle = GetComponent<Meta_VehicleBase>();
            Interact?.action.Enable();
        }

        public void OnDisable()
        {
            Interact?.action.Disable();
        }

        [ClientCallback]
        public void Update()
        {
            // TODO: Check Empty Seat And Run Cmds    
            if (CanEnter && Interact.action.WasPressedThisFrame())
            {
                Intracted = !Intracted;
                if (Intracted)
                {
                    CmdTakeControl();
                }
                else
                {
                    CmdReleaseControl();
                }
            }
        }

        // =========================================================================
        // Enter / Exit Vehicle
        // =========================================================================

        [Command(requiresAuthority = false)]
        public void CmdEnterVehicle(NetworkConnectionToClient _Conn = null)
        {
            _Conn.authenticationData = _Conn.identity.gameObject;
            (int _SeatIndex, Transform _SeatTransform) _FreeSeat = Vehicle.GetFreeSeat(); // صندلی خالی را پیدا کن
            if (_FreeSeat._SeatIndex == -1) // صندلی خالی پیدا نشد
            {
                Debug.Log("No Free Seat Found");
                return;
            }
            Debug.Log(_FreeSeat);
            SeatID = _FreeSeat._SeatIndex;

            if (Vehicle.Seat.AllSeats[_FreeSeat._SeatIndex].IsDriverSeat)
            {
                _Conn.authenticationData = _Conn.identity.gameObject; // اگر صندلی راننده بود کنترل را به دست بگیر
                Vehicle.DriverNetId = _Conn.identity.netId;
            }

            Vehicle.MarkSeatOccupied(SeatID, _Conn.identity.netId); // صندلی اشغال شد
            PreviousPlayer = _Conn.identity.gameObject;

            ChangeControl();
            AttachMe();

            if (PreviousPlayer.TryGetComponent(out Meta_PlayerCore _PlayerCore))
            {
                _PlayerCore.enabled = false;
            }
            if (PreviousPlayer.TryGetComponent(out CharacterController _Controller))
            {
                _Controller.enabled = false;
            }
            if (PreviousPlayer.TryGetComponent(out Collider _Collider))
            {
                _Collider.enabled = false;
            }
            ServerAttachPlayer(PreviousPlayer, Vehicle.Seat.AllSeats[_FreeSeat._SeatIndex].SeatTransform, SeatOffset, Quaternion.identity);

            NetworkServer.ReplacePlayerForConnection(_Conn, gameObject, ReplacePlayerOptions.KeepAuthority);

            Entered = true;
        }
        [Command(requiresAuthority = false)]
        public void CmdExitVehicle(NetworkConnectionToClient _Conn = null)
        {
            if (Entered)
            {
                _Conn.authenticationData = null;
                Vector3 _Pos = transform.position + transform.right * ExitDistance + Vector3.up;
                PreviousPlayer.transform.SetPositionAndRotation(_Pos, transform.rotation);

                if (PreviousPlayer.TryGetComponent(out Meta_PlayerCore _PlayerCore))
                {
                    _PlayerCore.enabled = true;
                }
                if (PreviousPlayer.TryGetComponent(out CharacterController _Controller))
                {
                    _Controller.enabled = true;
                }
                if (PreviousPlayer.TryGetComponent(out Collider _Collider))
                {
                    _Collider.enabled = true;
                }

                PreviousPlayer.transform.SetParent(null);

                if (Vehicle.Seat.AllSeats[SeatID].IsDriverSeat)
                {
                    _Conn.authenticationData = null; // اگر صندلی راننده بود کنترل را آزاد کن
                    Vehicle.DriverNetId = 0;
                }

                Vehicle.MarkSeatFree(SeatID);
                NetworkServer.ReplacePlayerForConnection(connectionToClient, PreviousPlayer, ReplacePlayerOptions.KeepActive);
                SeatID = -1;
                Entered = false;
            }
        }

        public virtual void AttachMe()
        {
            PreviousPlayer.transform.SetParent(Vehicle.Seat.AllSeats[SeatID].SeatTransform);
            PreviousPlayer.transform.localPosition = SeatOffset;
            PreviousPlayer.transform.localRotation = Quaternion.identity;
            Debug.Log("ATTACH");
        }

        public virtual void ChangeControl()
        {
            if (PreviousPlayer.TryGetComponent(out Meta_PlayerCore _PlayerCore))
            {
                _PlayerCore.enabled = false;
            }
            if (PreviousPlayer.TryGetComponent(out CharacterController _Controller))
            {
                _Controller.enabled = false;
            }
            if (PreviousPlayer.TryGetComponent(out Collider _Collider))
            {
                _Collider.enabled = false;
            }
            Debug.Log("Disable");
        }

        // =========================================================================
        // take Control And Release Control [Driver Only]
        // =========================================================================

        [Server]
        public void ServerAttachPlayer(GameObject _Player, Transform _Parent, Vector3 _LocalPos, Quaternion _LocalRot)
        {
            _Player.transform.SetParent(_Parent);
            _Player.transform.localPosition = _LocalPos;
            _Player.transform.localRotation = _LocalRot;
            //NetworkServer.SendToAll( message: { netId = _Player.GetComponent<NetworkIdentity>().netId });
        }

        [Command(requiresAuthority = false)]
        public void CmdTakeControl(NetworkConnectionToClient _Conn = null)
        {
            if (connectionToClient != null)
            {
                Debug.Log("[Base Vehicle] The Vehicle Already Have Player Inside");
                return;
            }
            _Conn.authenticationData = _Conn.identity.gameObject;



            Vehicle.DriverNetId = _Conn.identity.netId;

            PreviousPlayer = _Conn.identity.gameObject;



            // TODO: turn off player collider and movement

            if (PreviousPlayer.TryGetComponent(out Meta_PlayerCore _PlayerCore))

            {

                _PlayerCore.enabled = false;

            }

            if (PreviousPlayer.TryGetComponent(out CharacterController _Controller))

            {

                _Controller.enabled = false;

            }

            if (PreviousPlayer.TryGetComponent(out Collider _Collider))

            {

                _Collider.enabled = false;

            }

            else

            {

                Debug.Log("[Base Vehicle] Error");



            }



            (int _SeatIndex, Transform _SeatTransform) _FreeSeat = Vehicle.GetFreeSeat();

            Vehicle.MarkSeatOccupied(_FreeSeat._SeatIndex, Vehicle.DriverNetId);



            ServerAttachPlayer(PreviousPlayer, Vehicle.Seat.AllSeats[_FreeSeat._SeatIndex].SeatTransform, SeatOffset, Quaternion.identity);



            //NetworkServer.ReplacePlayerForConnection(_Conn, gameObject, ReplacePlayerOptions.KeepAuthority);

        }

        [Command(requiresAuthority = false)]

        public void CmdReleaseControl()

        {

            if (connectionToClient.authenticationData is GameObject _Player)

            {

                Vector3 _Pos = transform.position + transform.right * ExitDistance + Vector3.up;

                _Player.transform.SetPositionAndRotation(_Pos, transform.rotation);



                // If Driver Empty Driver Seat and DriverNetId = 0;



                connectionToClient.authenticationData = null;



                // TODO: turn on player collider and movement

                if (PreviousPlayer.TryGetComponent(out Meta_PlayerCore _PlayerCore))

                {

                    _PlayerCore.enabled = true;

                }

                if (PreviousPlayer.TryGetComponent(out CharacterController _Controller))

                {

                    _Controller.enabled = true;

                }

                if (PreviousPlayer.TryGetComponent(out Collider _Collider))

                {

                    _Collider.enabled = true;

                }

                PreviousPlayer.transform.SetParent(null);



                (int _SeatIndex, Transform _SeatTransform) _FreeSeat = Vehicle.GetFreeSeat();

                Vehicle.MarkSeatFree(_FreeSeat._SeatIndex);



                //NetworkServer.ReplacePlayerForConnection(connectionToClient, _Player, ReplacePlayerOptions.KeepActive);

            }

        }

        private void OnTriggerEnter(Collider other)
        {
            if (!isClient || !other.gameObject.CompareTag("Player")) return;

            if (other.TryGetComponent(out NetworkIdentity _NetId))
            {
                if (_NetId == NetworkClient.localPlayer)
                {
                    // TODO: check for empty seat
                    CanEnter = true;
                }
            }
        }

        public void OnTriggerExit(Collider other)
        {
            if (!isClient || !other.gameObject.CompareTag("Player")) return;

            if (other.TryGetComponent(out NetworkIdentity _NetId))
            {
                if (_NetId == NetworkClient.localPlayer)
                {
                    // TODO: leave the vehicle and empty the seat
                    CanEnter = false;
                }
            }
        }
    }

}