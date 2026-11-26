using System.Collections.Generic;
using UnityEngine;

[System.Serializable]
public class Vehicle_Exhaust
{
    public List<GameObject> Exhausts = new();
    public List<ParticleSystem> exhaustParticles = new();

    public Vehicle_Exhaust(GameObject vehicle)
    {
        FindExhaust(vehicle);
    }

    public void FindExhaust(GameObject vehicle)
    {
        if (!vehicle) return;

        Transform[] exhaustPoints = vehicle.GetComponentsInChildren<Transform>(true);

        foreach (Transform t in exhaustPoints)
        {
            string name = t.name.ToLower();
            if (t.childCount > 0) continue;
            if (!name.Contains("exhaust")) continue;

            Exhausts.Add(t.gameObject);
        }
    }

    public void AddSmokeToExhausts()
    {
        foreach (var exhaust in Exhausts)
        {
            GameObject psObj = new GameObject("Exhaust_Smoke");
            psObj.transform.SetParent(exhaust.transform);
            psObj.transform.localPosition = Vector3.zero;
            psObj.transform.localRotation = Quaternion.identity;

            ParticleSystem ps = psObj.AddComponent<ParticleSystem>();

            // Particle Main
            var main = ps.main;
            main.startLifetime = 0.5f;
            main.startSize = 0.2f;
            main.startSpeed = 0.3f;
            main.startColor = new Color(0.4f, 0.4f, 0.4f, 0.3f);
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.loop = true;
            main.playOnAwake = false;

            // Emission
            var emission = ps.emission;
            emission.rateOverTime = 15f;

            // Shape
            var shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Circle;
            shape.radius = 0.05f;
            shape.arc = 360f;
            shape.rotation = new Vector3(0f, 0f, 0f);

            // Velocity
            var velocity = ps.velocityOverLifetime;
            velocity.enabled = true;
            velocity.space = ParticleSystemSimulationSpace.Local;
            velocity.z = -1f;

            // Renderer
            var renderer = ps.GetComponent<ParticleSystemRenderer>();
            renderer.material = new Material(Shader.Find("Particles/Standard Unlit"));
            renderer.material.color = new Color(0.4f, 0.4f, 0.4f, 0.3f);
            renderer.renderMode = ParticleSystemRenderMode.Billboard;

            exhaustParticles.Add(ps);
        }
    }

    public void EnableSmoke(bool state)
    {
        foreach (var ps in exhaustParticles)
        {
            if (ps == null) continue;

            if (state)
            {
                if (!ps.isPlaying) ps.Play();
            }
            else
            {
                if (ps.isPlaying) ps.Stop();
            }
        }
    }
}
