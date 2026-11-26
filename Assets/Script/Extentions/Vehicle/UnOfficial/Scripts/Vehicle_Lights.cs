using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

[System.Serializable]
public class Vehicle_Lights
{
    public List<GameObject> HeadLight = new();
    public List<GameObject> BrakeLight = new();
    public List<GameObject> LeftIndicator = new();
    public List<GameObject> RightIndicator = new();


    public Vehicle_Lights(GameObject vehicle)
    {
        FindLights(vehicle);
    }
    public void FindLights(GameObject vehicle)
    {
        if (!vehicle) return;

        Transform[] lights = vehicle.GetComponentsInChildren<Transform>(true);

        foreach (Transform l in lights)
        {
            string name = l.name.ToLower();

            if (l.childCount > 0) continue;

            if (name.Contains("head") && name.Contains("light"))
                HeadLight.Add(l.gameObject);
            if (name.Contains("side") && name.Contains("light"))
                HeadLight.Add(l.gameObject);
            if (name.Contains("brake") && name.Contains("light"))
                BrakeLight.Add(l.gameObject);
            if (name.Contains("left") && name.Contains("signal"))
                LeftIndicator.Add(l.gameObject);
            if (name.Contains("right") && name.Contains("signal"))
                RightIndicator.Add(l.gameObject);
            //if (name.Contains("side") && name.Contains("light"))
            //    RightIndicator.Add(l.gameObject);
        }
    }
    public void AddLight(List<GameObject> lights, Color color, LightType type, float range = 10f, float angle = 30f)
    {
        if (lights.Count == 0) return;

        foreach (GameObject l in lights)
        {
            if (!l.GetComponent<Light>())
            {
                Light light = l.gameObject.AddComponent<Light>();
                light.type = type;
                light.range = range;
                if (type == LightType.Spot)
                {
                    light.spotAngle = angle;

                }
                light.color = color;
                //light.intensity = 0;
                light.enabled = false;
            }
        }
    }
    public void AutoLight(GameObject vehicle)
    {
        FindLights(vehicle);
        AddLight(HeadLight, Color.white, LightType.Spot,10,100f);
        AddLight(BrakeLight, Color.red, LightType.Point, 10f);
        AddLight(LeftIndicator, Color.yellow, LightType.Point, 10f);
        AddLight(RightIndicator, Color.yellow, LightType.Point, 10f);
    }
    public void SetLightsIntensity(bool turnOn)
    {
        float targetIntensity = turnOn ? 1f : 0f; // you can customize default "on" intensity

        void SetListIntensity(List<GameObject> lights)
        {
            foreach (var go in lights)
            {
                var light = go.GetComponent<Light>();
                if (light != null)
                {
                    if (turnOn)
                        light.intensity = 10;
                    else
                        light.intensity = 0f;
                }
            }
        }

        SetListIntensity(HeadLight);
        SetListIntensity(BrakeLight);
        SetListIntensity(LeftIndicator);
        SetListIntensity(RightIndicator);
    }
}
