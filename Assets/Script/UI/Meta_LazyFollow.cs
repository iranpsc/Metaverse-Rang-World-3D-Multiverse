using Mirror;
using System.Collections;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.UI;

namespace Meta
{
    [AddComponentMenu("Meta/Meta_LazyFollow")]
    [HelpURL("https://github.com/DreamFaver")]
    public class Meta_LazyFollow : MonoBehaviour
    {
        public LazyFollow LazyFollow;
        private IEnumerator Start()
        {
            if (LazyFollow == null)
            {
                Debug.LogError("Lazy Follow is Null");
                yield break;
            }

            yield return new WaitUntil(() => Camera.main != null);

            LazyFollow.target = Camera.main.transform;
        }
    }
}