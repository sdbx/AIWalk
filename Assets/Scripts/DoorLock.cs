using UnityEngine;
using UnityEngine.Events;

public class DoorLock : MonoBehaviour
{
  public UnityEvent OnUnlocked;
  public UnityEvent OnInvalid;


  public bool locked = true;

  [SerializeField]
  private NetworkController networkController;
  [SerializeField]
  private string password;

  public void Unlock(string inputPassword)
  {
    if (password != inputPassword)
    {
      networkController.SendEvent("OnInvalid");
      return;
    }
    locked = false;

    networkController.SendEvent("OnUnlocked");
  }
}