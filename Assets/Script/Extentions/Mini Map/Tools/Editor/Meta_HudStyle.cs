#if UNITY_EDITOR
using UnityEngine;
using UnityEngine.UI;
using UnityEditor;
using UnityEngine.Events;

namespace Meta.EditorTools
{
    /// <summary>
    /// SHARED HUD STYLE for the three Meta modules (MiniMap radar, DayNight controls,
    /// CameraForce controls). One accent, one panel language, procedural sprites - no
    /// external assets, no default white Unity buttons.
    ///
    /// Accent: CYAN #7EE0FF (used for rims, active states, slider fills).
    /// Player blip: #7CFF6B with a dark outline.
    /// </summary>
    public static class Meta_HudStyle
    {
        // ---- Style tokens (section E) ----
        public static readonly Color PanelBG = Hex(0x0E1218, 0.92f);   // dark glass
        public static readonly Color Bezel = Hex(0x12151A, 1f);       // metal bezel
        public static readonly Color BezelEdge = Hex(0x1E242E, 1f);
        public static readonly Color Accent = Hex(0x7EE0FF, 1f);      // cyan accent
        public static readonly Color AccentSoft = Hex(0x7EE0FF, 0.55f);
        public static readonly Color TextPrimary = Hex(0xE8ECF1, 1f);
        public static readonly Color TextMuted = Hex(0x8B93A1, 1f);
        public static readonly Color ButtonNormal = Hex(0x1A2030, 1f);
        public static readonly Color ButtonHover = Hex(0x243044, 1f);
        public static readonly Color Danger = Hex(0xFF5A5A, 1f);
        public static readonly Color BlipGreen = Hex(0x7CFF6B, 1f);
        public static readonly Color BlipOutline = Hex(0x0B0D10, 1f);
        public static readonly Color Void = Hex(0x0B0E13, 1f);        // radar empty space

        // ---- Type scale ----
        public const int TitleSize = 14;
        public const int BodySize = 12;
        public const int MicroSize = 10;

        // =====================================================================
        // Canvas / EventSystem
        // =====================================================================
        public static Canvas EnsureCanvas(string name, int sortingOrder)
        {
            var go = GameObject.Find(name);
            if (go != null && go.GetComponent<Canvas>() != null) return go.GetComponent<Canvas>();

            go = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(go, name);
            var canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = sortingOrder;
            go.AddComponent<CanvasScaler>();
            go.AddComponent<GraphicRaycaster>();
            return canvas;
        }

        public static void EnsureEventSystem()
        {
            if (UnityEngine.EventSystems.EventSystem.current != null) return;

            var es = new GameObject("EventSystem");
            Undo.RegisterCreatedObjectUndo(es, "Create EventSystem");
            es.AddComponent<UnityEngine.EventSystems.EventSystem>();
            var inputMod = System.Type.GetType(
                "UnityEngine.InputSystem.UI.InputSystemUIInputModule, Unity.InputSystem")
                ?? System.Type.GetType(
                "UnityEngine.EventSystems.StandaloneInputModule, UnityEngine.UI");
            if (inputMod != null) es.AddComponent(inputMod);
        }

        // =====================================================================
        // Panels / layers
        // =====================================================================
        /// <summary>Rounded dark-glass card. Returns the Image (its GameObject carries the RectTransform).</summary>
        public static Image Card(Transform parent, string name, Vector2 size, Vector2 anchor, Vector2 pivot, Vector2 offset, Color? bg = null)
        {
            var go = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(go, name);
            go.transform.SetParent(parent, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = anchor;
            rt.pivot = pivot;
            rt.anchoredPosition = offset;
            rt.sizeDelta = size;

            var img = go.AddComponent<Image>();
            img.sprite = Rounded(96, 14);
            img.type = Image.Type.Sliced;
            img.color = bg ?? PanelBG;
            img.raycastTarget = false;
            return img;
        }

        /// <summary>Stretched child layer (shadow / dim / border) on a card.</summary>
        public static Image Layer(Transform parent, string name, Sprite sprite, Color color, Vector2 size, Vector2 offset, bool sliced = false)
        {
            var go = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(go, name);
            go.transform.SetParent(parent, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = size;
            rt.anchoredPosition = offset;

            var img = go.AddComponent<Image>();
            img.sprite = sprite;
            img.type = sliced ? Image.Type.Sliced : Image.Type.Simple;
            img.color = color;
            img.raycastTarget = false;
            return img;
        }

        /// <summary>Thin 1px-style outline used as a cheap border around cards.</summary>
        public static Image Border(Transform parent, Vector2 size, Vector2 offset = default, Color? color = null)
        {
            var img = Layer(parent, "Border", Rounded(96, 14), color ?? Hex(0x2A323E, 0.85f), size, offset, true);
            var rt = img.rectTransform;
            var inner = Layer(parent, "BorderInner", Rounded(96, 14), color ?? Hex(0x2A323E, 0.85f), size - new Vector2(2f, 2f), offset);
            inner.rectTransform.SetAsFirstSibling();
            return img;
        }

        public static Text Label(Transform parent, string name, string text, int size, Color color, TextAnchor align = TextAnchor.MiddleLeft)
        {
            var go = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(go, name);
            go.transform.SetParent(parent, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = rt.offsetMax = Vector2.zero;

            var t = go.AddComponent<Text>();
            t.text = text;
            t.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            t.fontSize = size;
            t.color = color;
            t.alignment = align;
            t.raycastTarget = false;
            return t;
        }

        /// <summary>Compact dark chip button + PERSISTENT listener (works in Play mode).</summary>
        public static Button Chip(Transform parent, string text, UnityAction action, int height = 26, bool accent = false)
        {
            var go = new GameObject(text);
            Undo.RegisterCreatedObjectUndo(go, text);
            go.transform.SetParent(parent, false);

            var bg = go.AddComponent<Image>();
            bg.sprite = Rounded(64, 10);
            bg.type = Image.Type.Sliced;
            bg.color = ButtonNormal;
            bg.raycastTarget = true;

            var btn = go.AddComponent<Button>();
            btn.targetGraphic = bg;
            var colors = btn.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = Tint(ButtonHover);
            colors.pressedColor = Tint(Hex(0x141A26, 1f));
            colors.selectedColor = Color.white;
            colors.disabledColor = new Color(0.45f, 0.45f, 0.5f, 0.6f);
            colors.fadeDuration = 0.06f;
            btn.colors = colors;

            var le = go.AddComponent<LayoutElement>();
            le.preferredHeight = height;
            le.minHeight = height;
            le.flexibleWidth = 1f;

            var label = Label(go.transform, "Text", text, BodySize, accent ? Accent : TextMuted, TextAnchor.MiddleCenter);
            label.rectTransform.offsetMin = new Vector2(6f, 0f);
            label.rectTransform.offsetMax = new Vector2(-6f, 0f);

            if (action != null)
                UnityEditor.Events.UnityEventTools.AddVoidPersistentListener(btn.onClick, action);
            return btn;
        }

        /// <summary>Text-only (quiet) button - for secondary actions like Reset.</summary>
        public static Button TextButton(Transform parent, string text, UnityAction action, int height = 20)
        {
            var btn = Chip(parent, text, action, height, false);
            var bg = btn.targetGraphic as Image;
            if (bg != null) { bg.sprite = null; bg.color = Hex(0x141A26, 0.55f); }
            var label = btn.GetComponentInChildren<Text>(true);
            if (label != null) label.color = TextMuted;
            return btn;
        }

        /// <summary>Thin accent slider (track + fill + handle).</summary>
        public static Slider ThinSlider(Transform parent, string name, float min, float max, float value, int height = 18)
        {
            var go = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(go, name);
            go.transform.SetParent(parent, false);
            var rt = go.AddComponent<RectTransform>();
            rt.sizeDelta = new Vector2(220f, height);

            var slider = go.AddComponent<Slider>();
            slider.minValue = min;
            slider.maxValue = max;
            slider.wholeNumbers = false;
            slider.SetValueWithoutNotify(value);

            var le = go.AddComponent<LayoutElement>();
            le.preferredHeight = height;
            le.minHeight = height;

            var track = Layer(go.transform, "Track", Rounded(32, 6), Hex(0x161C26, 1f), new Vector2(220f, 6f), Vector2.zero, true);
            var fillArea = new GameObject("Fill Area");
            Undo.RegisterCreatedObjectUndo(fillArea, "Fill Area");
            fillArea.transform.SetParent(go.transform, false);
            var faRect = fillArea.AddComponent<RectTransform>();
            faRect.anchorMin = Vector2.zero;
            faRect.anchorMax = Vector2.one;
            faRect.offsetMin = new Vector2(0f, -3f);
            faRect.offsetMax = new Vector2(-14f, 3f);
            var fill = Layer(fillArea.transform, "Fill", Rounded(32, 6), AccentSoft, new Vector2(220f, 6f), Vector2.zero, true);
            fill.rectTransform.anchorMin = Vector2.zero;
            fill.rectTransform.anchorMax = new Vector2(0f, 1f);
            fill.rectTransform.offsetMin = Vector2.zero;
            fill.rectTransform.offsetMax = Vector2.zero;

            var handleArea = new GameObject("Handle Slide Area");
            Undo.RegisterCreatedObjectUndo(handleArea, "Handle Slide Area");
            handleArea.transform.SetParent(go.transform, false);
            var haRect = handleArea.AddComponent<RectTransform>();
            haRect.anchorMin = Vector2.zero;
            haRect.anchorMax = Vector2.one;
            haRect.offsetMin = new Vector2(7f, 0f);
            haRect.offsetMax = new Vector2(-7f, 0f);
            var handle = Layer(handleArea.transform, "Handle", Circle(64, 1.2f), Accent, new Vector2(14f, 14f), Vector2.zero);
            var handleRect = handle.rectTransform;

            slider.fillRect = fill.rectTransform;
            slider.handleRect = handleRect;
            slider.targetGraphic = handle;
            slider.direction = Slider.Direction.LeftToRight;
            return slider;
        }

        /// <summary>Small monospace-ish readout block (status).</summary>
        public static Text Readout(Transform parent, string name, string initial, int height = 74)
        {
            var go = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(go, name);
            go.transform.SetParent(parent, false);
            var rt = go.AddComponent<RectTransform>();
            rt.sizeDelta = new Vector2(220f, height);

            var le = go.AddComponent<LayoutElement>();
            le.preferredHeight = height;
            le.minHeight = height;

            var text = go.AddComponent<Text>();
            text.text = initial;
            text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            text.fontSize = MicroSize;
            text.color = TextMuted;
            text.alignment = TextAnchor.UpperLeft;
            text.raycastTarget = false;
            return text;
        }

        // =====================================================================
        // Procedural sprites
        // =====================================================================
        public static Sprite Circle(int size, float softness)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { name = $"MM_Circle_{size}" };
            var px = new Color[size * size];
            float c = size * 0.5f, r = c - 1f;
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float dx = (x + 0.5f) - c, dy = (y + 0.5f) - c;
                float d = Mathf.Sqrt(dx * dx + dy * dy);
                float a = Mathf.Clamp01((r - d) / Mathf.Max(0.5f, softness) + 0.5f);
                px[y * size + x] = new Color(1f, 1f, 1f, a);
            }
            tex.SetPixels(px); tex.Apply();
            tex.wrapMode = TextureWrapMode.Clamp;
            return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, new Vector4(r, r, r, r));
        }

        public static Sprite Ring(int size, float thickness)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { name = $"MM_Ring_{size}" };
            var px = new Color[size * size];
            float c = size * 0.5f, r = c - 1f, mid = r - thickness * 0.5f;
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float dx = (x + 0.5f) - c, dy = (y + 0.5f) - c;
                float d = Mathf.Sqrt(dx * dx + dy * dy);
                float a = Mathf.Clamp01((thickness * 0.5f - Mathf.Abs(d - mid)) + 0.5f);
                px[y * size + x] = new Color(1f, 1f, 1f, a);
            }
            tex.SetPixels(px); tex.Apply();
            tex.wrapMode = TextureWrapMode.Clamp;
            return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, new Vector4(r, r, r, r));
        }

        /// <summary>Rounded rectangle for sliced cards/chips.</summary>
        public static Sprite Rounded(int size, int radius)
        {
            return Rounded(size, radius, 1f);
        }

        /// <summary>Rounded rectangle with a feathered edge (soft shadow / glow).</summary>
        public static Sprite Rounded(int size, int radius, float softness)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                name = $"MM_Rounded_{size}_{radius}" + (softness > 1.01f ? "_soft" : "")
            };
            var px = new Color[size * size];
            float c = size * 0.5f;
            float hx = c - 0.5f;
            float r = Mathf.Min(radius, hx);
            float soft = Mathf.Max(0.5f, softness);

            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                // Signed distance to a rounded rectangle
                float dx = Mathf.Abs((x + 0.5f) - c) - (hx - r);
                float dy = Mathf.Abs((y + 0.5f) - c) - (hx - r);
                float outside = new Vector2(Mathf.Max(dx, 0f), Mathf.Max(dy, 0f)).magnitude;
                float inside = Mathf.Min(Mathf.Max(dx, dy), 0f);
                float d = outside + inside;                       // < 0 inside, > 0 outside

                float a = Mathf.Clamp01(0.5f - d / soft);
                px[y * size + x] = new Color(1f, 1f, 1f, a);
            }
            tex.SetPixels(px);
            tex.Apply();
            tex.wrapMode = TextureWrapMode.Clamp;
            return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f, 0,
                SpriteMeshType.FullRect, new Vector4(r, r, r, r));
        }

        /// <summary>Chevron pointing UP, bright green body + dark outline (colours are baked).</summary>
        public static Sprite Blip(int size)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { name = "MM_Blip" };
            var px = new Color[size * size];
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float u = (x + 0.5f) / size, v = (y + 0.5f) / size;      // v: 0 bottom -> 1 top
                float halfW = Mathf.Lerp(0.46f, 0f, v);
                float dx = Mathf.Abs(u - 0.5f);
                bool inside = v > 0.06f && dx <= halfW;
                bool inner = inside && dx <= halfW * 0.62f;
                px[y * size + x] = inner ? BlipGreen : (inside ? BlipOutline : new Color(0f, 0f, 0f, 0f));
            }
            tex.SetPixels(px); tex.Apply();
            tex.wrapMode = TextureWrapMode.Clamp;
            return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect);
        }

        public static Color Hex(uint rgb, float a = 1f)
        {
            return new Color(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, a);
        }

        /// <summary>Button tint multiplier so a hovered chip shifts from #1A2030 to #243044.</summary>
        public static Color Tint(Color target)
        {
            return new Color(
                target.r / Mathf.Max(0.0001f, ButtonNormal.r),
                target.g / Mathf.Max(0.0001f, ButtonNormal.g),
                target.b / Mathf.Max(0.0001f, ButtonNormal.b),
                1f);
        }
    }
}
#endif