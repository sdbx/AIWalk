using System;
using UnityEngine;

public class NetworkIdentity : MonoBehaviour
{
  [SerializeField]
  private string id;
  public string Id
  {
    get
    {
      if (string.IsNullOrEmpty(id)) throw new ArgumentException("NetworkIdentity.Id must not be empty.");
      return id;
    }
  }

  [SerializeField]
  private bool resetOnAwake = false;

  private void Awake()
  {
    if (resetOnAwake)
      SetId(GenerateId());
  }

  void Start()
  {
    if (string.IsNullOrEmpty(id)) throw new ArgumentException("NetworkIdentity.Id must not be empty.");
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
