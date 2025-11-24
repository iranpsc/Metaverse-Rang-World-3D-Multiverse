using System.Collections.Generic;
using UnityEngine;

[System.Serializable]
public class Vehicle_Seats
{
    public List<GameObject> DriverSeat = new();
    public List<GameObject> PassengerSeat = new();
    

    public Vehicle_Seats(GameObject vehicle)
    {
        FindSeat(vehicle);
    }
    public void FindSeat(GameObject vehicle)
    {
        if (!vehicle) return;

        Transform[] seats = vehicle.GetComponentsInChildren<Transform>(true);

        foreach (Transform t in seats)
        {
            string name = t.name.ToLower();

            if (!name.Contains("seat") || t.childCount > 0) continue;

            if (name.Contains("driver") || name.Contains("pilot") && name.Contains("seat"))
                DriverSeat.Add(t.gameObject);
            if (name.Contains("passenger") && name.Contains("seat"))
                PassengerSeat.Add(t.gameObject);
        }
    }
}