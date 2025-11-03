using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.UI;

public class Sample : Button
{
    // Audio properties
    [Header("Audio Settings")]
    [SerializeField] private AudioClip hoverSound;
    [SerializeField] private AudioClip clickSound;
    [SerializeField] private AudioSource audioSource;

    // Hover events
    [Header("Hover Events")]
    [SerializeField] private UnityEvent onHoverEnter = new UnityEvent();
    [SerializeField] private UnityEvent onHoverExit = new UnityEvent();

    // Public accessors for our events
    public UnityEvent OnHoverEnter => onHoverEnter;
    public UnityEvent OnHoverExit => onHoverExit;

    // Cache whether we're currently hovering to avoid duplicate event calls
    private bool isHovering = false;

    protected override void Awake()
    {
        base.Awake();

        // If no audio source is assigned, try to get one from the GameObject
        if (audioSource == null)
        {
            audioSource = GetComponent<AudioSource>();

            // If still null, add an AudioSource component
            if (audioSource == null)
            {
                audioSource = gameObject.AddComponent<AudioSource>();
                audioSource.playOnAwake = false;
            }
        }
    }

    // Override OnPointerEnter to add our custom hover functionality
    public override void OnPointerEnter(PointerEventData eventData)
    {
        base.OnPointerEnter(eventData);

        if (!isHovering)
        {
            isHovering = true;

            // Play hover sound if assigned
            if (hoverSound != null && audioSource != null)
            {
                audioSource.clip = hoverSound;
                audioSource.Play();
            }

            // Invoke hover enter event
            onHoverEnter?.Invoke();
        }
    }

    // Override OnPointerExit to handle hover exit
    public override void OnPointerExit(PointerEventData eventData)
    {
        base.OnPointerExit(eventData);

        if (isHovering)
        {
            isHovering = false;

            // Invoke hover exit event
            onHoverExit?.Invoke();
        }
    }

    // Override OnPointerClick to add click sound
    public override void OnPointerClick(PointerEventData eventData)
    {
        base.OnPointerClick(eventData);

        // Play click sound if assigned
        if (clickSound != null && audioSource != null)
        {
            audioSource.clip = clickSound;
            audioSource.Play();
        }
    }
}
