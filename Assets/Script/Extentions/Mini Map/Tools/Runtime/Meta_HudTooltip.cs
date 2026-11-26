using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Meta.Tools
{
    /// <summary>
    /// Minimal uGUI tooltip for the Meta control cards: a short plain-language explanation that
    /// appears while the pointer rests on a control.
    ///
    /// The label is a CHILD of the control, so there is no shared tooltip manager to coordinate and
    /// nothing to clean up between Setup runs. The EventTrigger is wired in Awake, which is why this
    /// works in Play mode without any editor-side persistent listener plumbing.
    ///
    /// Note on language: the cards use English labels on purpose. Legacy uGUI Text does not do
    /// Arabic script shaping, so Persian labels would render as disconnected, mis-ordered letters.
    /// </summary>
    [AddComponentMenu("Meta/Tools/HUD Tooltip")]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(EventTrigger))]
    public class Meta_HudTooltip : MonoBehaviour
    {
        [SerializeField, TextArea] private string _message = "";
        [SerializeField] private Text _label;
        [SerializeField] private CanvasGroup _group;
        [SerializeField] private bool _below = true;
        [SerializeField] private float _offset = 6f;
        [SerializeField] private Vector2 _size = new Vector2(200f, 38f);

        public string Message
        {
            get => _message;
            set { _message = value; if (_label != null) _label.text = value; }
        }

        /// <summary>Editor-time configuration used by the Setup tools.</summary>
        public void Configure(string message, Text label, CanvasGroup group, bool below, Vector2 size)
        {
            _message = message;
            _label = label;
            _group = group;
            _below = below;
            _size = size;

            if (_label != null) _label.text = message;
            Layout();
        }

        private void Awake()
        {
            EnsureTrigger();
            if (_label != null) _label.text = _message;
            Hide();
        }

        private void EnsureTrigger()
        {
            var trigger = GetComponent<EventTrigger>();
            if (trigger == null) trigger = gameObject.AddComponent<EventTrigger>();

            bool hasEnter = false, hasExit = false;
            for (int i = 0; i < trigger.triggers.Count; i++)
            {
                var id = trigger.triggers[i].eventID;
                if (id == EventTriggerType.PointerEnter) hasEnter = true;
                else if (id == EventTriggerType.PointerExit) hasExit = true;
            }

            if (!hasEnter)
            {
                var enter = new EventTrigger.Entry { eventID = EventTriggerType.PointerEnter };
                enter.callback.AddListener(_ => Show());
                trigger.triggers.Add(enter);
            }

            if (!hasExit)
            {
                var exit = new EventTrigger.Entry { eventID = EventTriggerType.PointerExit };
                exit.callback.AddListener(_ => Hide());
                trigger.triggers.Add(exit);
            }
        }

        public void Show()
        {
            if (_group == null) return;
            if (_label != null) _label.text = _message;
            _group.alpha = 1f;
            _group.blocksRaycasts = false;
            _group.interactable = false;
        }

        public void Hide()
        {
            if (_group == null) return;
            _group.alpha = 0f;
            _group.blocksRaycasts = false;
            _group.interactable = false;
        }

        private void Layout()
        {
            if (_label == null) return;
            var rt = _label.transform.parent as RectTransform;
            if (rt == null) return;

            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, _below ? 0f : 1f);
            rt.pivot = new Vector2(0.5f, _below ? 1f : 0f);
            rt.anchoredPosition = new Vector2(0f, _below ? -_offset : _offset);
            rt.sizeDelta = _size;
        }
    }
}