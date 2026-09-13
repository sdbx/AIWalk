using UnityEngine;

public class NetworkIdentity : MonoBehaviour
{
  [SerializeField]
  private string id;
  public string Id { get => id; }
}
