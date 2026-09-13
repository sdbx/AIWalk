
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;

public class CursorManager : MonoBehaviour
{
  [SerializeField]
  private InputActionAsset inputActions;
  [SerializeField]
  private string playerActionMapName = "Player";
  [SerializeField]
  private string uiActionMapName = "UI";

  InputActionMap playerActionMap;
  InputActionMap uiActionMap;

  void Awake()
  {
    if (Instance != null && Instance != this)
    {
      Destroy(gameObject);
      return;
    }
    Instance = this;

    playerActionMap = inputActions.FindActionMap(playerActionMapName);
    if (playerActionMap == null)
    {
      Debug.LogError($"Action Map '{playerActionMapName}' could not be found.", this);
      enabled = false;
    }
    uiActionMap = inputActions.FindActionMap(uiActionMapName);
    if (uiActionMap == null)
    {
      Debug.LogError($"Action Map '{uiActionMapName}' could not be found.", this);
      enabled = false;
    }

    ApplyMode();
  }

  [SerializeField]
  private CursorMode mode = CursorMode.UI;
  public CursorMode Mode
  {
    get => mode;
    set
    {
      if (mode == value) return;
      mode = value;
      ApplyMode();
    }
  }

  void ApplyMode()
  {
    switch (mode)
    {
      case CursorMode.UI:
        playerActionMap?.Disable();
        uiActionMap?.Enable();
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
        break;
      case CursorMode.FPS:
        uiActionMap?.Disable();
        playerActionMap?.Enable();
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
        break;
    }
  }

  public void SetUIMode()
  {
    Mode = CursorMode.UI;
  }

  public void SetFpsMode()
  {
    Mode = CursorMode.FPS;
  }

  public void ToggleMode()
  {
    if (mode == CursorMode.UI) mode = CursorMode.FPS;
    else if (mode == CursorMode.FPS) mode = CursorMode.UI;
  }

  public static CursorManager Instance { get; private set; }

  [Serializable]
  public enum CursorMode
  {
    UI,
    FPS,
  }
}
