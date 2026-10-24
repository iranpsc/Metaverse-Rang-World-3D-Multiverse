using System.Threading.Tasks;
using RTLTMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Network_A.Voice.Client.Participants
{
    public sealed class VoiceParticipantListItemView : MonoBehaviour
    {
        [Header("Participant UI")]
        [SerializeField] private RTLTextMeshPro userNameText;

        [Header("Mic UI")]
        [SerializeField] private Button micButton;
        [SerializeField] private Image micImage;
        [SerializeField] private Sprite micOnSprite;
        [SerializeField] private Sprite micOffSprite;

        [Header("Speaker UI")]
        [SerializeField] private Button speakerButton;
        [SerializeField] private Image speakerImage;
        [SerializeField] private Sprite speakerOnSprite;
        [SerializeField] private Sprite speakerOffSprite;

        public string UserId { get; private set; }
        public string UserName { get; private set; }
        public bool MicEnabled { get; private set; } = true;
        public bool SpeakerEnabled { get; private set; } = true;

        private VoiceDirectionalControlClient directionalControlClient;
        private bool micChangeInFlight;
        private bool speakerChangeInFlight;

        private void OnEnable()
        {
            SubscribeButtons();
        }

        private void OnDisable()
        {
            UnsubscribeButtons();
        }

        public bool Bind(
            string userId,
            string userName,
            VoiceDirectionalControlClient controlClient)
        {
            if (!ValidateReferences() || controlClient == null) return false;

            string normalizedUserId = Normalize(userId);
            string normalizedUserName = Normalize(userName);
            if (normalizedUserId.Length == 0 || normalizedUserName.Length == 0) return false;

            directionalControlClient = controlClient;
            UserId = normalizedUserId;
            SetUserName(normalizedUserName);
            InitializeButtonStates();
            return true;
        }

        public void SetUserName(string userName)
        {
            string normalizedUserName = Normalize(userName);
            if (normalizedUserName.Length == 0 || userNameText == null) return;

            UserName = normalizedUserName;
            userNameText.text = UserName;
        }

        private void SubscribeButtons()
        {
            if (micButton != null) micButton.onClick.AddListener(HandleMicClicked);
            if (speakerButton != null) speakerButton.onClick.AddListener(HandleSpeakerClicked);
        }

        private void UnsubscribeButtons()
        {
            if (micButton != null) micButton.onClick.RemoveListener(HandleMicClicked);
            if (speakerButton != null) speakerButton.onClick.RemoveListener(HandleSpeakerClicked);
        }

        private void InitializeButtonStates()
        {
            MicEnabled = true;
            SpeakerEnabled = true;
            micChangeInFlight = false;
            speakerChangeInFlight = false;
            ApplyMicVisualState();
            ApplySpeakerVisualState();
            ApplyButtonInteractableState();
        }

        private void HandleMicClicked()
        {
            if (micChangeInFlight) return;
            _ = ApplyMicStateAsync(!MicEnabled);
        }

        private async Task ApplyMicStateAsync(bool enabled)
        {
            micChangeInFlight = true;
            ApplyButtonInteractableState();

            try
            {
                bool sent = await directionalControlClient.SetOutgoingToUserAsync(UserId, enabled);
                if (!sent) return;

                MicEnabled = enabled;
                ApplyMicVisualState();
            }
            finally
            {
                micChangeInFlight = false;
                ApplyButtonInteractableState();
            }
        }

        private void HandleSpeakerClicked()
        {
            if (speakerChangeInFlight) return;
            _ = ApplySpeakerStateAsync(!SpeakerEnabled);
        }

        private async Task ApplySpeakerStateAsync(bool enabled)
        {
            speakerChangeInFlight = true;
            ApplyButtonInteractableState();

            try
            {
                bool sent = await directionalControlClient.SetIncomingFromUserAsync(UserId, enabled);
                if (!sent) return;

                SpeakerEnabled = enabled;
                ApplySpeakerVisualState();
            }
            finally
            {
                speakerChangeInFlight = false;
                ApplyButtonInteractableState();
            }
        }

        private void ApplyMicVisualState()
        {
            if (micImage != null) micImage.sprite = MicEnabled ? micOnSprite : micOffSprite;
        }

        private void ApplySpeakerVisualState()
        {
            if (speakerImage != null) speakerImage.sprite = SpeakerEnabled ? speakerOnSprite : speakerOffSprite;
        }

        private void ApplyButtonInteractableState()
        {
            if (micButton != null) micButton.interactable = !micChangeInFlight;
            if (speakerButton != null) speakerButton.interactable = !speakerChangeInFlight;
        }

        private bool ValidateReferences()
        {
            bool valid =
                userNameText != null &&
                micButton != null &&
                micImage != null &&
                micOnSprite != null &&
                micOffSprite != null &&
                speakerButton != null &&
                speakerImage != null &&
                speakerOnSprite != null &&
                speakerOffSprite != null;

            if (valid) return true;

            Debug.LogError("VOICE_PARTICIPANT_ITEM_REFERENCE_MISSING", this);
            return false;
        }

        private static string Normalize(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
        }
    }
}
