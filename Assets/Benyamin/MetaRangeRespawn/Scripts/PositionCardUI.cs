using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace MetaRange.Avatar
{
    public class PositionCardUI : MonoBehaviour
    {
        [Header("View Mode")]
        [SerializeField] private TextMeshProUGUI idLabel;
        [SerializeField] private TextMeshProUGUI posLabel;
        [SerializeField] private TextMeshProUGUI dateLabel;
        [SerializeField] private Button editButton;

        [Header("Edit Mode")]
        [Tooltip("فیلد نام یونیک — در حالت ویرایش قابل تغییر و ذخیره در JSON")]
        [SerializeField] private TMP_InputField nameField;
        [SerializeField] private GameObject editRow;
        [SerializeField] private TMP_InputField xField;
        [SerializeField] private TMP_InputField yField;
        [SerializeField] private TMP_InputField zField;
        [Tooltip("چرخش آواتار بر حسب درجه (eulerAngles) — در حالت ویرایش زنده به‌روز می‌شود")]
        [SerializeField] private TMP_InputField rxField;
        [SerializeField] private TMP_InputField ryField;
        [SerializeField] private TMP_InputField rzField;
        [SerializeField] private Button confirmButton;
        [SerializeField] private Button cancelButton;

        [Header("Edit Options")]
        [SerializeField] private bool moveAvatarOnConfirm = true;

        private string env;
        private string posId;
        private PositionEntry entry;
        private OwnerPanel owner;

        public void Setup(string environment, string id, PositionEntry e, OwnerPanel panel)
        {
            env = environment;
            posId = id;
            entry = e;
            owner = panel;

            if (idLabel != null) idLabel.text = id;

            if (posLabel != null && e != null && e.position != null)
                posLabel.text = string.Format("X:{0:F2}  Y:{1:F2}  Z:{2:F2}",
                    e.position.x, e.position.y, e.position.z);

            if (dateLabel != null)
                dateLabel.text = !string.IsNullOrEmpty(e?.updatedAt) ? e.updatedAt : "";

            if (editButton != null)
            {
                editButton.onClick.RemoveListener(OnEdit);
                editButton.onClick.AddListener(OnEdit);
            }
            if (confirmButton != null)
            {
                confirmButton.onClick.RemoveListener(OnConfirm);
                confirmButton.onClick.AddListener(OnConfirm);
            }
            if (cancelButton != null)
            {
                cancelButton.onClick.RemoveListener(OnCancel);
                cancelButton.onClick.AddListener(OnCancel);
            }

            SetMode(false);
        }

        private void OnDestroy()
        {
            if (editButton != null) editButton.onClick.RemoveListener(OnEdit);
            if (confirmButton != null) confirmButton.onClick.RemoveListener(OnConfirm);
            if (cancelButton != null) cancelButton.onClick.RemoveListener(OnCancel);
        }

        /// <summary>
        /// ورود به حالت ویرایش — **فقط UI محلی، بدون هیچ درخواست شبکه**.
        /// فیلدهای X/Y/Z به‌صورت زنده از موقعیت فعلی بازیکن خوانده می‌شوند
        /// (پیش‌نمایش) و ذخیره فقط با «تأیید» انجام می‌گیرد.
        /// </summary>
        private void OnEdit()
        {
            isEditing = true;

            // نام فعلی در فیلد ویرایش نمایش داده شود
            if (nameField != null) nameField.text = posId;

            if (owner != null)
            {
                owner.NotifyEditStarted(this);
                owner.SetRegisterStatus("ویرایش: فیلدها زنده از موقعیت بازیکن — ذخیره فقط با «تأیید»",
                                        new Color(0.85f, 0.90f, 1f));
            }

            SetMode(true);
            PullFromAvatar();
        }

        private void Update()
        {
            // فقط کارتی که در حال ویرایش است، هر فریم از بازیکن مقدار می‌گیرد
            if (!isEditing) return;
            PullFromAvatar();
        }

        /// <summary>
        /// خواندن موقعیت و چرخش لحظه‌ای بازیکن در فیلدها (بدون شبکه).
        /// اگر کاربر داخل یک فیلد تایپ کرده باشد، همان فیلد دست‌نخورده می‌ماند
        /// تا نوشتهٔ او پاک نشود.
        /// </summary>
        private void PullFromAvatar()
        {
            if (isBusy) return;                       // هنگام ذخیره‌سازی froze بماند
            if (owner == null || !owner.HasAvatar) return;

            Vector3 p = owner.AvatarPosition;
            SetField(xField, p.x);
            SetField(yField, p.y);
            SetField(zField, p.z);

            // چرخش هم زنده از بازیکن خوانده می‌شود (درجه)
            Vector3 e = owner.AvatarRotation;
            SetField(rxField, e.x);
            SetField(ryField, e.y);
            SetField(rzField, e.z);
        }

        static void SetField(TMP_InputField field, float value)
        {
            if (field == null) return;
            if (field.isFocused) return;              // تایپ کاربر پاک نشود
            field.text = value.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
        }

        private bool isBusy;
        private bool isEditing;      // کارت در حالت ویرایش است ⇒ فیلدها زنده از بازیکن

        private void OnConfirm()
        {
            if (isBusy) return;
            isEditing = false;          // دیگر از بازیکن نخوان؛ مقدار Inputها ملاک است
            StartCoroutine(ConfirmRoutine());
        }

        /// <summary>
        /// ذخیرهٔ تغییرات کارت: اول نام یونیک (در صورت تغییر) و بعد مختصات.
        /// اگر نام تکراری باشد، کارت در حالت ویرایش می‌ماند تا کاربر نام دیگری بزند.
        /// </summary>
        private System.Collections.IEnumerator ConfirmRoutine()
        {
            isBusy = true;
            if (confirmButton != null) confirmButton.interactable = false;

            // ---------- ۱) نام یونیک ----------
            string typedName = OwnerPanel.SanitizePositionName(nameField != null ? nameField.text : null);
            if (!string.IsNullOrEmpty(typedName) && typedName != posId)
            {
                bool renamed = false;
                yield return owner.RenamePosition(env, posId, typedName, ok => renamed = ok);

                if (!renamed)
                {
                    isBusy = false;
                    if (confirmButton != null) confirmButton.interactable = true;
                    yield break;   // خطا — در حالت ویرایش می‌مانیم
                }
                posId = typedName;
            }

            // ---------- ۲) مختصات ----------
            float x = OwnerPanel.ParseFloat(xField != null ? xField.text : null, 0f);
            float y = OwnerPanel.ParseFloat(yField != null ? yField.text : null, 0f);
            float z = OwnerPanel.ParseFloat(zField != null ? zField.text : null, 0f);

            // ---------- ۳) چرخش (درجه) ----------
            // اگر فیلدهای چرخش وجود نداشته باشند (پریفب قدیمی)، چرخش ثبت‌شده حفظ می‌شود
            Quaternion rot = ReadRotation();

            yield return owner.UpdatePositionRoutine(env, posId, new Vector3(x, y, z), rot, moveAvatarOnConfirm);

            isBusy = false;
            if (confirmButton != null) confirmButton.interactable = true;

            if (owner != null) owner.NotifyEditEnded(this);
            SetMode(false);   // لیست با مقادیر جدید دوباره ساخته می‌شود
        }

        /// <summary>
        /// ساخت Quaternion از فیلدهای Rx/Ry/Rz (درجه).
        /// اگر هر سه فیلد نال باشند ⇒ چرخش قبلیِ ثبت‌شده برگردانده می‌شود.
        /// </summary>
        private Quaternion ReadRotation()
        {
            bool hasFields = rxField != null || ryField != null || rzField != null;

            if (!hasFields)
                return (entry != null && entry.rotation != null)
                    ? entry.rotation.ToQuaternion()
                    : Quaternion.identity;

            float rx = OwnerPanel.ParseFloat(rxField != null ? rxField.text : null, 0f);
            float ry = OwnerPanel.ParseFloat(ryField != null ? ryField.text : null, 0f);
            float rz = OwnerPanel.ParseFloat(rzField != null ? rzField.text : null, 0f);

            return Quaternion.Euler(rx, ry, rz);   // ZYX (هم‌ترتیب Unity)
        }

        private void OnCancel()
        {
            isEditing = false;
            if (owner != null) owner.NotifyEditEnded(this);
            SetMode(false);   // بدون هیچ ذخیره‌سازی — مقادیر قبلی از سرور باقی می‌مانند
        }

        /// <summary>خروج از حالت ویرایش از بیرون (وقتی کارت دیگری ویرایش می‌شود)</summary>
        public void ExitEditMode()
        {
            isEditing = false;
            if (owner != null) owner.NotifyEditEnded(this);
            SetMode(false);
        }

        private void SetMode(bool edit)
        {
            if (editRow != null) editRow.SetActive(edit);
            if (confirmButton != null) confirmButton.gameObject.SetActive(edit);
            if (cancelButton != null) cancelButton.gameObject.SetActive(edit);
            if (editButton != null) editButton.gameObject.SetActive(!edit);

            // نام موقعیت در حالت ویرایش هم دیده شود (بالای فیلدها)
            if (idLabel != null) idLabel.gameObject.SetActive(true);
            if (posLabel != null) posLabel.gameObject.SetActive(!edit);
            if (dateLabel != null) dateLabel.gameObject.SetActive(!edit);

            // هایلایت کارتِ در حال ویرایش
            Image bg = GetComponent<Image>();
            if (bg != null) bg.color = edit ? new Color(0.22f, 0.42f, 0.68f, 0.98f)
                                            : new Color(0.18f, 0.20f, 0.25f, 0.95f);

            // چیدمان کارت/لیست بعد از تغییر حالت دوباره محاسبه شود
            if (transform is RectTransform rt)
                LayoutRebuilder.MarkLayoutForRebuild(rt);
        }
    }
}
