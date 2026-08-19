using Network_A.Auth;
using UnityEngine;

public class Meta_Logout : MonoBehaviour
{
    private GlobalAuthManager Manager;

    private void Awake()
    {
        Manager = GlobalAuthManager.Instance;
    }
    public void Logout()
    {
        if (Manager != null)
            Manager.Logout();
    }
}
