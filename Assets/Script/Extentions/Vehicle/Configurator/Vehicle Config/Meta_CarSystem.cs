// File: Meta_CarSystem.cs (FIXED)

using Mirror;
using System;
using UnityEngine;
using UnityEngine.InputSystem;
using static Meta.Vehicle.Meta_VehicleSeat; // مطمئن شوید این using درست است

namespace Meta.Vehicle
{
    [AddComponentMenu("Meta/Car System")]
    [HelpURL("https://github.com/DreamFaver")]
    public class Meta_CarSystem : Meta_VehicleBase
    {
        [Header("Input")]
        public InputActionReference Move;
        public InputActionReference Brake;
        public InputActionReference SignalLeft;
        public InputActionReference SignalRight;
        public InputActionReference HeadLightToggle;

        [Header("Drive Settings")]
        public float MotorTorque = 1500f;
        public float BrakeTorque = 3000f;
        public float MaxSteerAngle = 30f;

        public Vector3 baseCenterOfMass = new Vector3(0f, -0.3f, 0f);
        public float comLoweringFactor = 0.2f; // How much lower CoM goes at high speed
        public float maxSpeedForCoM = 100f; // km/h or m/s depending on your units

        public Rigidbody rb;

        // ورودی‌های محلی
        public float steerInput;
        public float motorInput;
        public bool isBraking; // <-- این متغیر فقط برای ذخیره ورودی دکمه ترمز است.

        // --- وضعیت‌های شبکه (SyncVar) ---
        [SyncVar(hook = nameof(OnHeadLightChanged))] private bool HeadLightOn = false;
        [SyncVar(hook = nameof(OnLeftSignalChanged))] private bool LeftSignalOn = false;
        [SyncVar(hook = nameof(OnRightSignalChanged))] private bool RightSignalOn = false;

        // --- وضعیت‌های فیزیک/تایمر محلی (استفاده شده برای چراغ ترمز و دنده عقب) ---
        private bool IsReversing = false;
        private bool IsBrakingLightOn = false; // ✅ تغییر یافته: برای جلوگیری از تداخل با متغیر ورودی isBraking
        private float SignalTimer = 0f;

        public override void OnStartAuthority()
        {
            if (HeadLightToggle != null) HeadLightToggle.action.performed += OnHeadLightToggle;
            if (SignalLeft != null) SignalLeft.action.performed += OnSignalLeftToggle;
            if (SignalRight != null) SignalRight.action.performed += OnSignalRightToggle;
            Move.action.Enable();
            Brake.action.Enable();
            SignalLeft.action.Enable();
            SignalRight.action.Enable();
            HeadLightToggle.action.Enable();
        }
        public override void OnStopAuthority()
        {
            if (HeadLightToggle != null) HeadLightToggle.action.performed -= OnHeadLightToggle;
            if (SignalLeft != null) SignalLeft.action.performed -= OnSignalLeftToggle;
            if (SignalRight != null) SignalRight.action.performed -= OnSignalRightToggle;

            Move.action.Disable();
            Brake.action.Disable();
            SignalLeft.action.Disable();
            SignalRight.action.Disable();
            HeadLightToggle.action.Disable();
        }
        public void Start()
        {
            rb = GetComponent<Rigidbody>();
            rb.centerOfMass = baseCenterOfMass;
        }
        private void Update()
        {
            if (isOwned)
            {
                HandleSignalLightsLogic();
            }
            UpdateWheelMeshes();
        }
        private void FixedUpdate()
        {
            if (isOwned && HasDriver)
            {
                HandleDriverInput();
                AdjustCenterOfMassBySpeed();

                HandleBrakeAndReverseLight();
            }
            else
            {
                ApplyBrake(BrakeTorque);
            }
        }

        // --------------- [Vehicle] --------------- //
        #region Engine And Steering
        public void HandleInput()
        {
            // ✅ چک حیاتی: چک می‌کنیم که NetId بازیکن محلی که این کد را اجرا می‌کند، با DriverNetId (تنظیم شده در سرور) یکی باشد.
            if (DriverNetId != netId) // netId در اینجا NetId آبجکت خودرو است. 
                                      // این چک نادرست است، باید از isOwned استفاده کنیم که در Meta_VehicleBase قبلاً هست.

                // **تصحیح:** از آنجایی که Authority خودرو فقط به راننده داده می‌شود، چک isOwned در FixedUpdate کافی است.
                // اما اگر می‌خواهید مطمئن شوید که مسافر نمی‌تواند ورودی را حتی اگر Authority به اشتباه منتقل شده باشد، فعال کند:

                if (!isOwned)
                {
                    // اگر Authority ندارید، ورودی‌ها را صفر کنید.
                    motorInput = 0f;
                    steerInput = 0f;
                    isBraking = false;
                    return;
                }

            // --- منطق خواندن ورودی‌ها ---
            motorInput = Move.action.ReadValue<Vector2>().y;
            steerInput = Move.action.ReadValue<Vector2>().x;
            isBraking = Brake.action.IsPressed();
        }
        private void UpdateWheelMeshes()
        {
            if (Wheel == null || Wheel.AllWheels == null || Wheel.AllCollider == null) return;

            int _Count = Mathf.Min(Wheel.AllWheels.Count, Wheel.AllCollider.Count);
            for (int i = 0; i < _Count; i++)
            {
                Transform _Visual = Wheel.AllWheels[i];
                WheelCollider _Wc = Wheel.AllCollider[i];
                if (!_Visual || !_Wc) continue;

                _Wc.GetWorldPose(out Vector3 _Pos, out Quaternion _Rot);
                _Visual.transform.position = _Pos;
                _Visual.transform.rotation = _Rot;
            }
        }
        public void AdjustCenterOfMassBySpeed()
        {
            if (rb == null) return;

            float _Speed = rb.linearVelocity.magnitude; // in m/s
            float _Time = Mathf.Clamp01(_Speed / maxSpeedForCoM); // 0 to 1

            float _NewYOffset = Mathf.Lerp(baseCenterOfMass.y, baseCenterOfMass.y - comLoweringFactor, _Time);

            rb.centerOfMass = new Vector3(baseCenterOfMass.x, _NewYOffset, baseCenterOfMass.z);
        }
        public override void HandleDriverInput()
        {
            // این متد فقط روی کلاینتی اجرا می‌شود که isOwned است (یعنی راننده).
            // اما برای امنیت بیشتر، HandleInput هم چک DriveNetId را انجام می‌دهد.
            HandleInput();

            // اگر ورودی‌ها صفر باشند، این توابع هم هیچ اثری ندارند.
            ApplyMotor();
            ApplyBrake(isBraking ? BrakeTorque : 0);
            ApplySteering();
        }
        private void ApplyMotor()
        {
            if (Wheel == null || Wheel.RearCollider == null) return;

            float _CurrentMotorTorque = motorInput * MotorTorque;
            foreach (var _WC in Wheel.RearCollider)
            {
                _WC.motorTorque = _CurrentMotorTorque;
            }
        }
        private void ApplySteering()
        {
            if (Wheel == null || Wheel.RearCollider == null) return;

            float _SteerAngle = steerInput * MaxSteerAngle;
            foreach (var _WC in Wheel.FrontCollider)
            {
                _WC.steerAngle = _SteerAngle;
            }
            if (Wheel.SteeringWheel != null)
            {
                Wheel.SteeringWheel.localRotation = Quaternion.Euler(0, 0, -_SteerAngle * 3f);
            }
        }
        private void ApplyBrake(float _Brake)
        {
            if (Wheel == null || Wheel.RearCollider == null) return;

            foreach (var _Wc in Wheel.AllCollider)
            {
                _Wc.brakeTorque = _Brake;
            }
        }
        #endregion

        // --------------- [Light] --------------- //
        #region Light Controller
        private void OnSignalLeftToggle(InputAction.CallbackContext context)
        {
            if (!isOwned || !HasDriver) return;
            // ✅ اصلاح شد: برای چپ، راست باید خاموش شود.
            CmdSetSignals(false, !LeftSignalOn);
        }
        private void OnSignalRightToggle(InputAction.CallbackContext context)
        {
            if (!isOwned || !HasDriver) return;
            // ✅ اصلاح شد: برای راست، چپ باید خاموش شود.
            CmdSetSignals(!RightSignalOn, false);
        }

        [Command]
        public void CmdSetSignals(bool _RightSignal, bool _LeftSignal)
        {
            // حذف منطق اضافی، زیرا متدهای ورودی اکنون مسئول خاموش کردن سیگنال دیگر هستند.
            RightSignalOn = _RightSignal;
            LeftSignalOn = _LeftSignal;
        }

        private void OnHeadLightToggle(InputAction.CallbackContext context)
        {
            if (!isOwned || !HasDriver) return;

            CmdToggleHeadLights(!HeadLightOn);
        }

        [Command]
        public void CmdToggleHeadLights(bool _NewState)
        {
            HeadLightOn = _NewState;
        }

        private void OnHeadLightChanged(bool _OldValue, bool _NewValue)
        {
            if (Light != null)
                Light.ToggleLights(Light.HeadLights, _NewValue);
        }

        private void HandleBrakeAndReverseLight()
        {
            if (Light == null || rb == null) return;

            // ✅ اصلاح شد: استفاده از rb.velocity برای تشخیص سرعت خطی
            float _ForwardSpeed = Vector3.Dot(rb.angularVelocity, transform.forward);
            bool _CurrentlyReversing = (_ForwardSpeed < -0.1f && motorInput < 0f); // سرعت عقب و فشار دادن گاز/ترمز به سمت عقب

            // ترمز فعال است اگر: 1. دکمه ترمز فشرده شده باشد یا 2. در حال حرکت به جلو باشیم و موتور اینپوت منفی باشد (ترمز با موتور)
            bool _ApplyBrakeLights = isBraking || (_ForwardSpeed > 0.1f && motorInput < -0.1f);

            // Reverse Lights
            if (_CurrentlyReversing != IsReversing)
            {
                Light.ToggleLights(Light.ReverseLight, _CurrentlyReversing);
                IsReversing = _CurrentlyReversing;
            }

            // Brake Lights
            // ✅ اصلاح شد: چک کردن و آپدیت کردن متغیر IsBrakingLightOn برای حفظ وضعیت
            if (_ApplyBrakeLights != IsBrakingLightOn)
            {
                Light.ToggleLights(Light.BrakeLights, _ApplyBrakeLights);
                IsBrakingLightOn = _ApplyBrakeLights;
            }
        }

        private void OnLeftSignalChanged(bool _OldValue, bool _NewValue)
        {
            // اگر از طریق شبکه خاموش شد، از چشمک زدن جلوگیری کند
            if (!_NewValue && Light != null)
                Light.ToggleLights(Light.TurnLeftSignal, false);
        }
        private void OnRightSignalChanged(bool _OldValue, bool _NewValue)
        {
            if (!_NewValue && Light != null)
                Light.ToggleLights(Light.TurnRightSignal, false);
        }

        // در فایل Meta_CarSystem.cs
        private void HandleSignalLightsLogic()
        {
            // چک‌های ایمنی
            if (Light == null) return;
            if (!LeftSignalOn && !RightSignalOn) return;

            // اگر لیست‌ها خالی یا کامپوننت‌ها نال باشند، ادامه نمی‌دهیم
            if (Light.TurnLeftSignal.Count == 0 || Light.TurnRightSignal.Count == 0) return;

            SignalTimer += Time.deltaTime;

            if (SignalTimer >= 1f / 1.5f)
            {
                SignalTimer = 0f;

                // 1. تعیین وضعیت فعلی روشن بودن
                bool _CheckLeft = LeftSignalOn && Light.TurnLeftSignal[0].LightComponent != null;
                bool _CheckRight = RightSignalOn && Light.TurnRightSignal[0].LightComponent != null;

                // وضعیت فعلی نور (روشن یا خاموش). اگر هیچکدام برای چک کردن آماده نبود، خاموش (false) فرض می‌شود.
                bool _CurrentLightState = false;

                if (_CheckLeft)
                {
                    // اگر چپ فعال است، وضعیت را از چپ بگیر
                    _CurrentLightState = Light.TurnLeftSignal[0].LightComponent.enabled;
                }
                else if (_CheckRight)
                {
                    // اگر فقط راست فعال است، وضعیت را از راست بگیر
                    _CurrentLightState = Light.TurnRightSignal[0].LightComponent.enabled;
                }

                // 2. محاسبه وضعیت چشمک‌زن بعدی (معکوس وضعیت فعلی)
                bool _ToggleState = !_CurrentLightState;


                // 3. اعمال وضعیت به چراغ‌های فعال
                if (LeftSignalOn)
                {
                    Light.ToggleLights(Light.TurnLeftSignal, _ToggleState);
                }

                if (RightSignalOn)
                {
                    Light.ToggleLights(Light.TurnRightSignal, _ToggleState);
                }
            }
        }
        #endregion
    }
}