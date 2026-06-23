using UnityEngine;

public class G7RemotePlayerView : MonoBehaviour
{
    [Header("Interpolation")]
    [SerializeField] private float positionLerpSpeed = 12f;
    [SerializeField] private float rotationLerpSpeed = 14f;
    [SerializeField] private float snapDistance = 8f;

    private Vector3 targetPosition;
    private Quaternion targetRotation = Quaternion.identity;
    private bool hasTarget;

    //* این تابع مقدار اولیه ریموت پلیر را تنظیم می کند تا کلون در لحظه ساخت در جای درست باشد.
    public void Initialize(Vector3 position, Quaternion rotation)
    {
        targetPosition = position;
        targetRotation = rotation;
        transform.SetPositionAndRotation(position, rotation);
        hasTarget = true;
    }

    //* این تابع وضعیت هدف جدید را از شبکه دریافت می کند و حرکت نرم در آپدیت انجام می شود.
    public void SetTargetState(Vector3 position, Quaternion rotation)
    {
        targetPosition = position;
        targetRotation = rotation;
        hasTarget = true;
    }

    //* این تابع هر فریم کلون را به آرامی به سمت آخرین وضعیت شبکه حرکت می دهد.
    private void Update()
    {
        if (!hasTarget) return;

        float distance = Vector3.Distance(transform.position, targetPosition);
        if (distance >= snapDistance)
        {
            transform.SetPositionAndRotation(targetPosition, targetRotation);
            return;
        }

        transform.position = Vector3.Lerp(transform.position, targetPosition, positionLerpSpeed * Time.deltaTime);
        transform.rotation = Quaternion.Slerp(transform.rotation, targetRotation, rotationLerpSpeed * Time.deltaTime);
    }
}
