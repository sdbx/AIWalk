using UnityEngine;

public class Door : MonoBehaviour
{
    [SerializeField] private Transform left;
    [SerializeField] private Transform right;

    [SerializeField] private float speed = 1f;
    [SerializeField] private bool isOpen = false;
    [SerializeField] private float distance = 1f;
    [SerializeField] private float CurrentDistance = 0f;
    
    private Vector3 initLeftPosition;
    private Vector3 initRightPosition;

    public void OpenDoor()
    {
        isOpen = true;
    }

    public void CloseDoor()
    {
        isOpen = false;
    }

    void Start()
    {
        initLeftPosition = left.localPosition;
        initRightPosition = right.localPosition;
    }
    

    void Update()
    {
        if (isOpen)
        {
            CurrentDistance = distance;
        }
        else
        {
            CurrentDistance = 0;
        }
        
        left.localPosition = Vector3.Lerp(left.localPosition, new Vector3(-CurrentDistance, 0, 0), Time.deltaTime * speed);
        right.localPosition = Vector3.Lerp(right.localPosition, new Vector3(CurrentDistance, 0, 0), Time.deltaTime * speed);
    }
}
