using System;
using AIWalk.Networking;
using UnityEngine;
using UnityEngine.Events;

public class DoorLock : NetBehaviour
{
  public UnityEvent OnUnlocked;
  public UnityEvent OnInvalid;

  public bool locked = true;

  [SerializeField]
  private string password;

  public void Unlock(string inputPassword)
  {
    if (IsHost)
    {
      if (!ValidPassword(inputPassword))
      {
        SendEvent("doorlock.invalid", EventAudience.All, "");
        return;
      }

      SendEvent("doorlock.unlocked", EventAudience.All, "");
    }
    else
    {
      SendEvent("doorlock.unlock", EventAudience.Host, new UnlockData(inputPassword));
    }
  }

  bool ValidPassword(string inputPassword)
  {
    return IsHost && password == inputPassword;
  }

  protected override void OnNetworkReady()
  {
    if (IsHost)
    {
      Subscribe("doorlock.unlock", e =>
      {
        var data = e.DeserializePayload<UnlockData>();
        Debug.Log($"password: {data.password}");

        Unlock(data.password);
      });
    }

    Subscribe("doorlock.unlocked", _ =>
    {
      _OnUnlocked();
    });
    Subscribe("doorlock.invalid", _ =>
    {
      _OnInvalid();
    });
  }

  void _OnUnlocked()
  {
    OnUnlocked.Invoke();
    locked = false;
  }
  void _OnInvalid()
  {
    OnInvalid.Invoke();
  }

  [Serializable]
  struct UnlockData
  {
    public string password;

    public UnlockData(string password)
    {
      this.password = password;
    }
  }
}