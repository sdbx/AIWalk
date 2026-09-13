using System;
using UnityEngine;

public class NetworkIdentity : MonoBehaviour
{
  [SerializeField]
  private string id;
  public string Id { get => id; }

  [SerializeField]
  private bool resetOnAwake = false;

  private void Awake()
  {
    if(resetOnAwake)
      SetId(GenerateId());
  }

  public static string GenerateId()
  {
    return Guid.NewGuid().ToString("N");
  }

  public void SetId(string id)
  {
    this.id = id;
  }
}
