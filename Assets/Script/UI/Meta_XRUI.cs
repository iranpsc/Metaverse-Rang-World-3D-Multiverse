using UnityEngine;
using UnityEngine.InputSystem;

public class Meta_XRUI : MonoBehaviour
{
    public Transform Head;
    public float SpawnDistance = 2f;
    public GameObject UIMenu;
    public InputActionReference ShowButton;

    public float DeactivateRange = 10f;

    public void OnEnable()
    {
        ShowButton.action.Enable();
        ShowButton.action.performed += Toggle;
    }
    public void OnDisable()
    {
        ShowButton.action.performed -= Toggle;
        ShowButton.action.Disable();
    }
    public void Update()
    {
        if (Head == null)
        {
            try
            {
                Head = Camera.main.transform;
            }
            catch
            {
                return;
            }
        }
        UIMenu.transform.LookAt(new Vector3(Head.position.x, UIMenu.transform.position.y, Head.position.z));
        UIMenu.transform.forward *= -1;
        if (UIMenu.activeSelf)
        {
            float Distance = Vector3.Distance(Head.position, UIMenu.transform.position);
            if (Distance > DeactivateRange)
            {
                UIMenu.SetActive(false);
            }
        }
    }
    public void Toggle(InputAction.CallbackContext _Ctx)
    {
        UIMenu.SetActive(!UIMenu.activeSelf);

        UIMenu.transform.position = Head.position + new Vector3(Head.forward.x, 0, Head.forward.z).normalized * SpawnDistance;

    }
}
