using Meta.Vehicle;
using Mirror;
using System;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Meta
{
    [AddComponentMenu("Meta/Vehicle System")]
    [DisallowMultipleComponent]
    [HelpURL("https://github.com/DreamFaver")]
    public class Meta_VehicleSystem : Meta_VehicleBase
    {
        [Serializable]
        public struct MoveKeys
        {
            public InputActionReference Move;
            public InputActionReference Brake;
            public InputActionReference SignalLeft;
            public InputActionReference SignalRight;
            public InputActionReference HeadLightToggle;
        }

        [SyncVar(hook = nameof(OnHeadLightChanged))] private bool HeadLightOn = false;
        [SyncVar(hook = nameof(OnLeftSignalChanged))] private bool LeftSignalOn = false;
        [SyncVar(hook = nameof(OnRightSignalChanged))] private bool RightSignalOn = false;

        public Rigidbody Rb;

        public MoveKeys moveKeys;

        [Header("Movemnt")]
        public float MotorTorque = 1500f;
        public float BrakeTorque = 3000f;
        public float MaxSteerAngle = 45f;

        float CoMLoweringFactor = 0.2f;
        float MaxSpeedForCoM = 100f;

        [Serializable]
        public struct RuntimeData
        {
            [ReadOnly, SerializeField] float _MotorInput;
            [ReadOnly, SerializeField] float _SteerInput;
            [ReadOnly, SerializeField] Vector3 _CenterOfMass;
            [ReadOnly, SerializeField] bool _IsBraking;
            [ReadOnly, SerializeField] bool _IsReversing;
            [ReadOnly, SerializeField] bool _IsBrakingLightOn;
            [ReadOnly, SerializeField] float _SignalTimer;

            public float MotorInput
            {
                get => _MotorInput;
                internal set => _MotorInput = value;
            }
            public float SteerInput
            {
                get => _SteerInput;
                internal set => _SteerInput = value;
            }
            public Vector3 CenterOfMass
            {
                get => _CenterOfMass;
                internal set => _CenterOfMass = value;
            }
            public bool IsBraking
            {
                get => _IsBraking;
                internal set => _IsBraking = value;
            }
            public bool IsReversing
            {
                get => _IsReversing;
                internal set => _IsReversing = value;
            }
            public bool IsBrakingLightOn
            {
                get => _IsBrakingLightOn;
                internal set => _IsBrakingLightOn = value;
            }
            public float SignalTimer
            {
                get => _SignalTimer;
                internal set => _SignalTimer = value;
            }

        }
        public RuntimeData runtimeData;

        #region Network Setup
        protected override void OnValidate()
        {
            if (Application.isPlaying) return;

            base.OnValidate();
            Reset();
        }
        protected virtual void Reset()
        {
            GetComponent<Rigidbody>().isKinematic = true;
            //this.enabled = false;
        }
        public override void OnStartAuthority()
        {
            Rb.isKinematic = false;

            this.enabled = true;

            if (moveKeys.HeadLightToggle != null) moveKeys.HeadLightToggle.action.performed += OnHeadLightToggle;
            if (moveKeys.SignalLeft != null) moveKeys.SignalLeft.action.performed += OnSignalLeftToggle;
            if (moveKeys.SignalRight != null) moveKeys.SignalRight.action.performed += OnSignalRightToggle;

            moveKeys.Move.action.Enable();
            moveKeys.Brake.action.Enable();
            moveKeys.SignalLeft.action.Enable();
            moveKeys.SignalRight.action.Enable();
            moveKeys.HeadLightToggle.action.Enable();
        }
        public override void OnStopAuthority()
        {
            GetComponent<Rigidbody>().isKinematic = true;
            this.enabled = false;

            if (moveKeys.HeadLightToggle != null) moveKeys.HeadLightToggle.action.performed -= OnHeadLightToggle;
            if (moveKeys.SignalLeft != null) moveKeys.SignalLeft.action.performed -= OnSignalLeftToggle;
            if (moveKeys.SignalRight != null) moveKeys.SignalRight.action.performed -= OnSignalRightToggle;

            moveKeys.Move.action.Disable();
            moveKeys.Brake.action.Disable();
            moveKeys.SignalLeft.action.Disable();
            moveKeys.SignalRight.action.Disable();
            moveKeys.HeadLightToggle.action.Disable();
        }
        #endregion

        private void Start()
        {
            Rb = GetComponent<Rigidbody>();
            Rb.centerOfMass = runtimeData.CenterOfMass;
        }

        private void Update()
        {
            if (Rb.isKinematic) return;
            if (isOwned)
            {
                HandleSignalLightsLogic();

                RotateWheel();
            }
        }
        private void FixedUpdate()
        {
            if (isOwned)
            {
                if (HasDriver)
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
        }
        // -------------------- [Vehicle] -------------------- //
        #region [Vehicle]
        public override void HandleDriverInput()
        {
            HandleInput();

            ApplyMotor();
            ApplySteering();
            ApplyBrake(runtimeData.IsBraking ? BrakeTorque : 0);
        }
        public virtual void HandleInput()
        {
            runtimeData.MotorInput = moveKeys.Move.action.ReadValue<Vector2>().y;
            runtimeData.SteerInput = moveKeys.Move.action.ReadValue<Vector2>().x;

            runtimeData.IsBraking = moveKeys.Brake.action.IsPressed();
        }
        public virtual void ApplyMotor()
        {
            if (Wheel == null || Wheel.RearCollider == null) return;

            float _CurrentMotorTorque = runtimeData.MotorInput * MotorTorque;

            foreach (WheelCollider _WC in Wheel.RearCollider)
            {
                _WC.motorTorque = _CurrentMotorTorque;
            }
        }
        public virtual void ApplySteering()
        {
            if (Wheel == null || Wheel.RearCollider == null) return;

            float _SteeringAngle = runtimeData.SteerInput * MaxSteerAngle;
            
            foreach (WheelCollider _Wc in Wheel.FrontCollider)
            {
                _Wc.steerAngle = _SteeringAngle;
            }
            if (Wheel.SteeringWheel != null)
            {
                Vector3 _FixedRot = Wheel.SteeringWheel.localEulerAngles;
                _FixedRot.z = -_SteeringAngle;
                Wheel.SteeringWheel.localEulerAngles = _FixedRot;
            }
        }
        public virtual void ApplyBrake(float _Brake)
        {
            if (Wheel == null || Wheel.RearCollider == null) return;

            foreach (WheelCollider _WC in Wheel.AllCollider)
            {
                _WC.brakeTorque = _Brake;
            }
        }
        public virtual void AdjustCenterOfMassBySpeed()
        {
            if (Rb == null) return;

            float _Speed = Rb.linearVelocity.magnitude;
            float _Time = Mathf.Clamp01(_Speed / MaxSpeedForCoM);

            float _NewOffset = Mathf.Lerp(runtimeData.CenterOfMass.y, runtimeData.CenterOfMass.y - CoMLoweringFactor, _Time);

            Rb.centerOfMass = new Vector3(runtimeData.CenterOfMass.x, _NewOffset, runtimeData.CenterOfMass.z);
        }
        public virtual void RotateWheel()
        {
            if (Wheel == null || Wheel.AllWheels == null || Wheel.AllCollider == null) return;

            int _Count = Mathf.Min(Wheel.AllWheels.Count, Wheel.AllCollider.Count);
            
            for (int i = 0; i < _Count; i++)
            {
                Transform _Visual = Wheel.AllWheels[i];
                WheelCollider _Wc = Wheel.AllCollider[i];
                if (!_Visual || !_Wc) continue;

                _Wc.GetWorldPose(out Vector3 _Pos, out Quaternion _Rot);
                _Visual.position = _Pos;
                _Visual.rotation = _Rot;
            }
        }
        #endregion

        // -------------------- [Light] -------------------- //
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

        [Command(requiresAuthority = false)]
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

        [Command(requiresAuthority = false)]
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
            if (Light == null || Rb == null) return;

            // ✅ اصلاح شد: استفاده از rb.velocity برای تشخیص سرعت خطی
            float _ForwardSpeed = Vector3.Dot(Rb.angularVelocity, transform.forward);
            bool _CurrentlyReversing = (_ForwardSpeed < -0.1f && runtimeData.MotorInput < 0f); // سرعت عقب و فشار دادن گاز/ترمز به سمت عقب

            // ترمز فعال است اگر: 1. دکمه ترمز فشرده شده باشد یا 2. در حال حرکت به جلو باشیم و موتور اینپوت منفی باشد (ترمز با موتور)
            bool _ApplyBrakeLights = runtimeData.IsBraking || (_ForwardSpeed > 0.1f && runtimeData.MotorInput < -0.1f);

            // Reverse Lights
            if (_CurrentlyReversing != runtimeData.IsReversing)
            {
                Light.ToggleLights(Light.ReverseLight, _CurrentlyReversing);
                runtimeData.IsReversing = _CurrentlyReversing;
            }

            // Brake Lights
            // ✅ اصلاح شد: چک کردن و آپدیت کردن متغیر IsBrakingLightOn برای حفظ وضعیت
            if (_ApplyBrakeLights != runtimeData.IsBrakingLightOn)
            {
                Light.ToggleLights(Light.BrakeLights, _ApplyBrakeLights);
                runtimeData.IsBrakingLightOn = _ApplyBrakeLights;
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

            runtimeData.SignalTimer += Time.deltaTime;

            if (runtimeData.SignalTimer >= 1f / 1.5f)
            {
                runtimeData.SignalTimer = 0f;

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