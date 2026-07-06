using NUnit.Framework;
using UnityEngine;

namespace Meta
{
    [AddComponentMenu("Meta/Meta_VehicleConfigBase")]
    [HelpURL("https://google.com")]
    public abstract class Meta_VehicleConfigBase : MonoBehaviour
    {
        protected abstract void Get();
        protected abstract void Set();
        public virtual void Config() { }
        public virtual void AutoConfig() { }
    }
}