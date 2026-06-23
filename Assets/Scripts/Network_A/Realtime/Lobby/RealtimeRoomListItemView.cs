using System;
using Network_A.Realtime.Lobby;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Network_A.Realtime.Lobby
{
    public class RealtimeRoomListItemView : MonoBehaviour
    {
        [SerializeField] private Button roomButton;
        [SerializeField] private TextMeshProUGUI roomNameText;

        private RealtimeRoomDto room;
        private Action<RealtimeRoomDto> clicked;

        private void Awake()
        {
            if (roomButton == null) roomButton = GetComponentInChildren<Button>(true);
            if (roomNameText == null && roomButton != null) roomNameText = roomButton.GetComponentInChildren<TextMeshProUGUI>(true);

            if (roomButton != null)
            {
                roomButton.onClick.RemoveListener(HandleClick);
                roomButton.onClick.AddListener(HandleClick);
            }
        }

        public void Setup(RealtimeRoomDto room, Action<RealtimeRoomDto> clicked)
        {
            this.room = room;
            this.clicked = clicked;

            if (this.room != null) this.room.Normalize();

            string title = this.room == null || string.IsNullOrWhiteSpace(this.room.roomName)
                ? "Room"
                : this.room.roomName;

            if (roomNameText != null) roomNameText.text = title;
            if (roomButton != null) roomButton.interactable = this.room != null && this.room.CanJoin();
        }

        public void SetInteractable(bool value)
        {
            if (roomButton == null) return;
            roomButton.interactable = value && room != null && room.CanJoin();
        }

        private void HandleClick()
        {
            if (room == null) return;
            clicked?.Invoke(room);
        }
    }
}