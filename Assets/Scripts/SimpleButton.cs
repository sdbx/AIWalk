using UnityEngine;
using UnityEngine.Events;

public class SimpleButton : MonoBehaviour
{
  public UnityEvent OnPressed;
  public UnityEvent OnReleased;

  [SerializeField]
  private GameObject buttonTop;

  [SerializeField]
  private Vector3 buttonTopPressedPosition;
  [SerializeField]
  private float animationSpeed = 5f;

  Vector3 initialPosition;

  public bool pressed = false;

  void Start()
  {
    initialPosition = buttonTop.transform.localPosition;
  }

  void Update()
  {
    var targetPosition = pressed ? buttonTopPressedPosition : initialPosition;
    buttonTop.transform.localPosition = Vector3.Lerp(buttonTop.transform.localPosition, targetPosition, Time.deltaTime * animationSpeed);
  }

  public void Press()
  {
    pressed = true;
    OnPressed.Invoke();
  }

  public void Release()
  {
    pressed = false;
    OnReleased.Invoke();
  }

  public void Toggle()
  {
    if (pressed) Release();
    else Press();
  }
}