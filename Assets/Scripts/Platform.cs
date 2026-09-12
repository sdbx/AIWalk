using UnityEngine;

public class Platform : MonoBehaviour
{
   [SerializeField] private float speed = 1f;
   [SerializeField] private Vector2 Limits = new Vector2(0.1f, 1f);

   [SerializeField] private Rigidbody platformFloor;
   [SerializeField] private float targetHeight = 1f;

    private void Start()
    {

    }


    public void moveTo(float height)
    {
        targetHeight = height;
    }

    public void moveHigh()
    {
        targetHeight = Limits.y;
    }

    public void moveLow()
    {
        targetHeight = Limits.x;
    }


    private void FixedUpdate()
    {
        targetHeight = Mathf.Clamp(targetHeight, Limits.x, Limits.y);

        Transform transform = platformFloor.transform;

        Vector3 currentLocal = transform.localPosition;

        Vector3 targetLocal = new Vector3(
            currentLocal.x,
            targetHeight,
            currentLocal.z
        );

        Vector3 nextLocal = Vector3.Lerp(
            currentLocal,
            targetLocal,
            Time.fixedDeltaTime * speed
        );

        Vector3 nextWorld = transform.parent.TransformPoint(nextLocal);

        platformFloor.MovePosition(nextWorld);
    }

}



