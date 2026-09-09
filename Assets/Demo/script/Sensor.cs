using UnityEngine;

public class Sensor : MonoBehaviour
{
    // Start is called once before the first execution of Update after the MonoBehaviour is created
    [SerializeField] private float timer = 0f;
    [SerializeField] private float timeToClose = 3f;
    [SerializeField] private float openTime = 5f;
    [SerializeField] private Door door;


    // Update is called once per frame
    void Update()
    {
        timer = Mathf.Max(timer-Time.deltaTime,0);
        if(timer <= 0)
        {
            door.CloseDoor();
        }
    }
    void OnTriggerEnter(Collider other)
    {
        door.OpenDoor();
        timer = openTime;
    }

    void OnTriggerExit(Collider other)
    {
        timer = timeToClose;
    }

}
