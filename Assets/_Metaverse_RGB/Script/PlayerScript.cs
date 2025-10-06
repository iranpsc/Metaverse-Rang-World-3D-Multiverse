using System;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

public class PlayerScript : MonoBehaviour
{
    [Header("Inputs")]
    [SerializeField] private InputActionReference MoveAction;
    [SerializeField] private InputActionReference RunAction;
    [SerializeField] private InputActionReference JumpAction;
    [SerializeField] private InputActionReference CrouchAction;

    [Header("Settings")]
    public float MoveSpeed = 2.0f;
    public float RunSpeed = 4.0f;

    [Header("Animations")]
    [SerializeField] private Animator Anim;

    public float CurrentPosZ;
    public float CurrentPosX;

    public float Acceleration = 1f;

    private Vector2 MoveInput;

    int HashPosX;
    int HashPosZ;

    private void Start()
    {
        HashPosX = Animator.StringToHash("PosX");
        HashPosZ = Animator.StringToHash("PosZ");
    }
    private void Update()
    {
        MoveInput = MoveAction.action.ReadValue<Vector2>();
        ValueAccelerate();
    }
    private void ValueAccelerate()
    {
        // Temp Variable
        bool _Run = RunAction.action.IsPressed();
        bool _Move = MoveInput.sqrMagnitude > 0.01f;
        float _CurrentSpeed = _Run ? RunSpeed : MoveSpeed;

        // Get Move and Run Speed
        CurrentPosX = MoveInput.x * _CurrentSpeed;
        CurrentPosZ = MoveInput.y * _CurrentSpeed;

        // Smooth Damping
        Anim.SetFloat(HashPosX, CurrentPosX, Acceleration, Time.deltaTime);
        Anim.SetFloat(HashPosZ, CurrentPosZ, Acceleration, Time.deltaTime);

        // Reset Value
        if(!_Move)
        {
            if (Mathf.Abs(Anim.GetFloat(HashPosX)) < 0.01f)
                Anim.SetFloat(HashPosX, 0f);
            if (Mathf.Abs(Anim.GetFloat(HashPosZ)) < 0.01f)
                Anim.SetFloat(HashPosZ, 0f);
        }
    }
}
