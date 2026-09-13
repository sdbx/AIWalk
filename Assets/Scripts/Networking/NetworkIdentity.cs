using System;
using UnityEngine;

public class NetworkIdentity : MonoBehaviour
{
  [SerializeField]
  private string id;
  public string Id { get => id; }

  public static string GenerateId()
  {
    return Guid.NewGuid().ToString("N");
  }
}
