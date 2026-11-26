using UnityEngine;

public class PlayerVehicleInteraction : MonoBehaviour
{
    public KeyCode interactKey = KeyCode.F;
    public float interactRange = 3f;
    public LayerMask vehicleLayer;
    public Vector3 seatOffset = Vector3.zero; // tweak in Inspector

    private GameObject currentVehicle;
    private MonoBehaviour controllerWithDriverFlag;
    private bool isInVehicle = false;
    private bool isDriver = false;
    private bool shouldHidePlayer = false;
    private Transform originalParent;

    private CharacterController characterController;
    private MonoBehaviour thirdPersonController;

    void Start()
    {
        characterController = GetComponent<CharacterController>();
        originalParent = transform.parent;

        // Replace with your actual movement script name
        thirdPersonController = GetComponent<ThirdPersonController>();
    }

    void Update()
    {
        if (Input.GetKeyDown(interactKey))
        {
            if (isInVehicle)
                ExitVehicle();
            else
                TryEnterNearbyVehicle();
        }
    }

    void TryEnterNearbyVehicle()
    {
        Collider[] hits = Physics.OverlapSphere(transform.position, interactRange, vehicleLayer);

        foreach (Collider hit in hits)
        {
            Vehicle_Core core = hit.GetComponentInParent<Vehicle_Core>();
            if (core == null) continue;

            MonoBehaviour controller = GetControllerWithHasDriver(core.gameObject);
            if (controller == null) continue;

            currentVehicle = core.gameObject;
            controllerWithDriverFlag = controller;

            bool hasDriver = (bool)controller.GetType().GetField("HasDriver").GetValue(controller);

            // Get hidePlayerOnEnter flag (default false)
            shouldHidePlayer = false;
            var hideField = controller.GetType().GetField("hidePlayerOnEnter");
            if (hideField != null && hideField.FieldType == typeof(bool))
                shouldHidePlayer = (bool)hideField.GetValue(controller);

            // Try driver seat
            GameObject availableDriverSeat = null;
            foreach (var seat in core.Seats.DriverSeat)
            {
                if (seat.transform.childCount == 0)
                {
                    availableDriverSeat = seat;
                    break;
                }
            }

            if (!hasDriver && availableDriverSeat != null)
            {
                EnterSeat(availableDriverSeat.transform, true);
                controller.GetType().GetField("HasDriver").SetValue(controller, true);
                return;
            }

            // Try passenger seat
            GameObject availablePassengerSeat = null;
            foreach (var seat in core.Seats.PassengerSeat)
            {
                if (seat.transform.childCount == 0)
                {
                    availablePassengerSeat = seat;
                    break;
                }
            }

            if (availablePassengerSeat != null)
            {
                EnterSeat(availablePassengerSeat.transform, false);
                return;
            }

            Debug.Log("No free seats in vehicle.");
        }
    }

    void EnterSeat(Transform seatTransform, bool asDriver)
    {
        isInVehicle = true;
        isDriver = asDriver;

        if (thirdPersonController != null)
            thirdPersonController.enabled = false;

        characterController.enabled = false;
        if (TryGetComponent(out Collider col)) col.enabled = false;

        transform.SetParent(seatTransform);
        transform.localPosition = seatOffset;
        transform.localRotation = Quaternion.identity;

        if (shouldHidePlayer)
        {
            foreach (var r in GetComponentsInChildren<Renderer>())
                r.enabled = false;
        }
    }

    void ExitVehicle()
    {
        isInVehicle = false;

        transform.SetParent(originalParent);
        transform.position = currentVehicle.transform.position + currentVehicle.transform.right * 2f;

        if (thirdPersonController != null)
            thirdPersonController.enabled = true;

        characterController.enabled = true;
        if (TryGetComponent(out Collider col)) col.enabled = true;

        if (isDriver && controllerWithDriverFlag != null)
            controllerWithDriverFlag.GetType().GetField("HasDriver").SetValue(controllerWithDriverFlag, false);

        if (shouldHidePlayer)
        {
            foreach (var r in GetComponentsInChildren<Renderer>())
                r.enabled = true;
        }

        currentVehicle = null;
        controllerWithDriverFlag = null;
        isDriver = false;
        shouldHidePlayer = false;
    }

    MonoBehaviour GetControllerWithHasDriver(GameObject vehicle)
    {
        MonoBehaviour[] all = vehicle.GetComponents<MonoBehaviour>();
        foreach (var comp in all)
        {
            if (comp == null) continue;
            var field = comp.GetType().GetField("HasDriver");
            if (field != null && field.FieldType == typeof(bool))
                return comp;
        }
        return null;
    }
}
