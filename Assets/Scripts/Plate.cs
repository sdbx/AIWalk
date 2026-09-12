using UnityEngine;
using UnityEngine.Events;

public class Plate : MonoBehaviour
{
    [SerializeField] 
    private float speed = 1f;
    [SerializeField]
    private float activationDistance = 1f;

    [SerializeField]
    private Transform innerPlate;

    [SerializeField]
    private bool isActivated = true;
    [SerializeField]
    private bool isPressed = false;

    [field: SerializeField]
    public UnityEvent OnPlatePressed { get; private set; } = new();

    [field: SerializeField]
    public UnityEvent OnPlateReleased { get; private set; } = new();

    private Vector3 initPosition;

    private void Awake()
    {
        initPosition = innerPlate.localPosition;
    }

    private void Update()
    {
        Vector3 targetPosition = isPressed || !isActivated
            ? initPosition + Vector3.down * activationDistance
            : initPosition;

        innerPlate.localPosition = Vector3.Lerp(innerPlate.localPosition, targetPosition, Time.deltaTime * speed);
    }

    private bool CheckIsPlayer(Collider other)
    {
        return other.attachedRigidbody != null && other.attachedRigidbody.CompareTag("Player");
    }

    private void OnTriggerEnter(Collider other)
    {
        if (CheckIsPlayer(other))
        {
            isPressed = true;
            OnPlatePressed.Invoke();
        }
    }

    private void OnTriggerExit(Collider other)
    {
        if (CheckIsPlayer(other))
        {
            isPressed = false;
            OnPlateReleased.Invoke();
        }
    }

    public void ActivatePlate()
    {
        isActivated = true;
    }

    public void DeactivatePlate()
    {
        isActivated = false;
    }
}
