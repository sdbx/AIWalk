using UnityEngine;


public class RandomTeleporter : MonoBehaviour
{
    [SerializeField]
    private Vector3 deltaRange = Vector3.zero;

    private void Awake()
    {
        Vector3 randomVector = new Vector3(
            Random.Range(-deltaRange.x, deltaRange.x),
            Random.Range(-deltaRange.y, deltaRange.y),
            Random.Range(-deltaRange.z, deltaRange.z)
        );
        Debug.Log($"Before: {transform.position}");
        var rigidbody = gameObject.GetComponent<Rigidbody>();
        if (rigidbody)
        {
            rigidbody.MovePosition(randomVector);
            return;
        }
        transform.position += randomVector;
    }

    private void Start()
{
    Debug.Log($"Start: {transform.position}");
}
}