using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class Vehicle_Lock : MonoBehaviour
{
    public bool IsLocked;
    public bool IsHandBreak;

    public void Update()
    {
        if (Input.GetKeyDown(KeyCode.V)) Lock();
        if(Input.GetKeyDown(KeyCode.X)) HandBreak();
    }
    public void Lock()
    {
        bool locking = IsLocked;
        IsLocked = !locking;
    }
    public void HandBreak()
    {
        bool handbreak = IsHandBreak;
        IsHandBreak = !handbreak;
    }
}
