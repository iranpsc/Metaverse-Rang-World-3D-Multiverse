using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

public class Vehicle_Light_Controller : MonoBehaviour
{
    public Vehicle_Core vehicle;

    private InputAction LeftAction;
    private InputAction RightAction;
    private InputAction HeadAction;
    private InputAction BrakeAction;

    private bool leftSignalOn = false;
    private bool rightSignalOn = false;
    private bool headLightOn = false;

    private Coroutine leftBlinkCoroutine;
    private Coroutine rightBlinkCoroutine;

    private void Start()
    {
        vehicle = GetComponent<Vehicle_Core>();
    }

    private void OnDestroy()
    {
        if (LeftAction != null) LeftAction.performed -= OnLeftSignal;
        if (RightAction != null) RightAction.performed -= OnRightSignal;
        if (HeadAction != null) HeadAction.performed -= OnHeadlight;
    }

    // --- 📌 این متد هنگام سوار شدن بازیکن call می‌شود ---
    public void InjectInput(InputAction left, InputAction right, InputAction head, InputAction brake)
    {
        LeftAction = left;
        RightAction = right;
        HeadAction = head;
        BrakeAction = brake;

        LeftAction.performed += OnLeftSignal;
        RightAction.performed += OnRightSignal;
        HeadAction.performed += OnHeadlight;
    }

    private void Update()
    {
        if (BrakeAction != null)
        {
            bool braking = BrakeAction.ReadValue<float>() > 0.1f;
            HandleBrakeLight(braking);
        }
    }

    private void OnLeftSignal(InputAction.CallbackContext ctx)
    {
        ToggleLeftSignal();
    }

    private void OnRightSignal(InputAction.CallbackContext ctx)
    {
        ToggleRightSignal();
    }

    private void OnHeadlight(InputAction.CallbackContext ctx)
    {
        ToggleHeadLight();
    }

    private void ToggleLeftSignal()
    {
        leftSignalOn = !leftSignalOn;
        rightSignalOn = false;

        if (rightBlinkCoroutine != null) StopCoroutine(rightBlinkCoroutine);
        if (leftBlinkCoroutine != null) StopCoroutine(leftBlinkCoroutine);

        SetLightState(vehicle.Lights.RightIndicator, false);

        if (leftSignalOn)
            leftBlinkCoroutine = StartCoroutine(Blink(vehicle.Lights.LeftIndicator));
        else
            SetLightState(vehicle.Lights.LeftIndicator, false);
    }

    private void ToggleRightSignal()
    {
        rightSignalOn = !rightSignalOn;
        leftSignalOn = false;

        if (leftBlinkCoroutine != null) StopCoroutine(leftBlinkCoroutine);
        if (rightBlinkCoroutine != null) StopCoroutine(rightBlinkCoroutine);

        SetLightState(vehicle.Lights.LeftIndicator, false);

        if (rightSignalOn)
            rightBlinkCoroutine = StartCoroutine(Blink(vehicle.Lights.RightIndicator));
        else
            SetLightState(vehicle.Lights.RightIndicator, false);
    }

    private void ToggleHeadLight()
    {
        headLightOn = !headLightOn;
        SetLightState(vehicle.Lights.HeadLight, headLightOn);
    }

    private void HandleBrakeLight(bool state)
    {
        SetLightState(vehicle.Lights.BrakeLight, state);
    }

    private IEnumerator Blink(List<GameObject> lights)
    {
        while (true)
        {
            SetLightState(lights, true);
            yield return new WaitForSeconds(0.5f);
            SetLightState(lights, false);
            yield return new WaitForSeconds(0.5f);
        }
    }

    private void SetLightState(List<GameObject> lights, bool state)
    {
        foreach (var obj in lights)
        {
            if (obj.TryGetComponent(out Light l))
                l.enabled = state;
        }
    }
}
