using UnityEngine;
using UnityEngine.InputSystem;

[RequireComponent(typeof(CharacterController))]
public class G7SimpleCylinderCharacterController : MonoBehaviour
{
    [Header("Movement")]
    [SerializeField] private float moveSpeed = 4.5f;
    [SerializeField] private float rotationSpeed = 160f;

    [Header("Jump And Gravity")]
    [SerializeField] private float jumpHeight = 1.5f;
    [SerializeField] private float gravity = -22f;
    [SerializeField] private float groundedStickForce = -2f;

    [Header("Input")]
    [SerializeField] private bool useKeyboard = true;
    [SerializeField] private bool useGamepad = true;

    private CharacterController characterController;
    private float verticalVelocity;
    private Vector2 moveInput;
    private bool jumpPressed;

    private void Awake()
    {
        characterController = GetComponent<CharacterController>();
    }

    private void Update()
    {
        ReadInput();
        HandleMovement();
    }

    private void ReadInput()
    {
        moveInput = Vector2.zero;
        jumpPressed = false;

        if (useKeyboard && Keyboard.current != null)
        {
            if (Keyboard.current.wKey.isPressed || Keyboard.current.upArrowKey.isPressed) moveInput.y += 1f;
            if (Keyboard.current.sKey.isPressed || Keyboard.current.downArrowKey.isPressed) moveInput.y -= 1f;
            if (Keyboard.current.dKey.isPressed || Keyboard.current.rightArrowKey.isPressed) moveInput.x += 1f;
            if (Keyboard.current.aKey.isPressed || Keyboard.current.leftArrowKey.isPressed) moveInput.x -= 1f;
            if (Keyboard.current.spaceKey.wasPressedThisFrame) jumpPressed = true;
        }

        if (useGamepad && Gamepad.current != null)
        {
            Vector2 gamepadMove = Gamepad.current.leftStick.ReadValue();
            if (gamepadMove.sqrMagnitude > moveInput.sqrMagnitude) moveInput = gamepadMove;
            if (Gamepad.current.buttonSouth.wasPressedThisFrame) jumpPressed = true;
        }

        moveInput = Vector2.ClampMagnitude(moveInput, 1f);
    }

    private void HandleMovement()
    {
        float moveValue = moveInput.y;
        float turnValue = moveInput.x;

        transform.Rotate(0f, turnValue * rotationSpeed * Time.deltaTime, 0f);

        Vector3 horizontalMove = transform.forward * moveValue * moveSpeed;
        UpdateVerticalVelocity();

        Vector3 finalMove = horizontalMove;
        finalMove.y = verticalVelocity;

        characterController.Move(finalMove * Time.deltaTime);
    }

    private void UpdateVerticalVelocity()
    {
        if (characterController.isGrounded)
        {
            if (verticalVelocity < 0f) verticalVelocity = groundedStickForce;
            if (jumpPressed) verticalVelocity = Mathf.Sqrt(jumpHeight * -2f * gravity);
            return;
        }

        verticalVelocity += gravity * Time.deltaTime;
    }
}