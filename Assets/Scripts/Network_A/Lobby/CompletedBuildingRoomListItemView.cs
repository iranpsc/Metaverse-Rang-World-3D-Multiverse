using System;
using Network_A.Core;
using Network_A.Lobby.Buildings;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Network_A.Lobby
{
    public sealed class CompletedBuildingRoomListItemView : MonoBehaviour
    {
        [Header("Room Item UI")]
        [SerializeField] private Button roomButton;
        [SerializeField] private TextMeshProUGUI buildingCodeText;

        private CompletedBuildingDto building;
        private Action<CompletedBuildingDto> clicked;

        public CompletedBuildingDto Building => building;

        //* این تابع دکمه آیتم را به تابع کلیک متصل می‌کند و ناقص‌بودن مراجع بازرس را گزارش می‌دهد.
        private void Awake()
        {
            if (roomButton == null) NetworkFileLogger.Error("LOBBY_BUILDING_ITEM_UI", "مرجع دکمه روم در بازرس تنظیم نشده است.");
            if (buildingCodeText == null) NetworkFileLogger.Error("LOBBY_BUILDING_ITEM_UI", "مرجع متن کد ساختمان در بازرس تنظیم نشده است.");

            if (roomButton != null)
            {
                roomButton.onClick.RemoveListener(HandleClick);
                roomButton.onClick.AddListener(HandleClick);
            }
        }

        //* این تابع هنگام نابودی آیتم، شنونده دکمه و اطلاعات ساختمان را آزاد می‌کند.
        private void OnDestroy()
        {
            if (roomButton != null) roomButton.onClick.RemoveListener(HandleClick);
            building = null;
            clicked = null;
        }

        //* این تابع ساختمان و تابع انتخاب را روی آیتم قرار می‌دهد و فقط کد ساختمان را نمایش می‌دهد.
        public void Setup(CompletedBuildingDto building, Action<CompletedBuildingDto> clicked)
        {
            this.building = building;
            this.clicked = clicked;

            if (this.building != null) this.building.Normalize();

            string buildingCode = this.building != null ? this.building.feature_properties_id : string.Empty;
            if (buildingCodeText != null) buildingCodeText.text = buildingCode;
            if (roomButton != null) roomButton.interactable = this.building != null && this.building.HasValidFeatureId();
        }

        //* این تابع امکان کلیک روی آیتم را بدون حذف اطلاعات ساختمان تغییر می‌دهد.
        public void SetInteractable(bool value)
        {
            if (roomButton == null) return;
            roomButton.interactable = value && building != null && building.HasValidFeatureId();
        }

        //* این تابع وضعیت انتخاب ظاهری دکمه را با ساختمان انتخاب‌شده هماهنگ می‌کند.
        public void SetSelected(bool selected)
        {
            if (roomButton == null) return;

            if (selected)
            {
                roomButton.Select();
                return;
            }

            if (EventSystem.current != null && EventSystem.current.currentSelectedGameObject == roomButton.gameObject) EventSystem.current.SetSelectedGameObject(null);
        }

        //* این تابع بررسی می‌کند آیتم مربوط به شناسه ساختمان داده‌شده باشد.
        public bool MatchesFeatureId(int featureId)
        {
            return building != null && building.feature_id == featureId;
        }

        //* این تابع کلیک دکمه را فقط در صورت وجود ساختمان معتبر به کنترلر صحنه تحویل می‌دهد.
        private void HandleClick()
        {
            if (building == null || !building.HasValidFeatureId()) return;
            clicked?.Invoke(building);
        }
    }
}
