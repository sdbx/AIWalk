using UnityEngine;
using UnityEngine.Events;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

[RequireComponent(typeof(PanelRenderer))]
public class PasswordPopupController : MonoBehaviour
{
  public UnityEvent OnPasswordConfirmed;
  public UnityEvent OnPasswordRejected;

  [SerializeField]
  private string answer;

  [SerializeField]
  private InputActionAsset inputActions;

  TextField passwordField;
  Button confirmButton;
  Button closeButton;

  PanelRenderer panelRenderer;
  VisualElement root;
  int uiVersion = -1;

  void OnEnable()
  {
    panelRenderer = GetComponent<PanelRenderer>();
    panelRenderer.RegisterUIReloadCallback(OnUIReload);
  }
  void OnDisable()
  {
    if (passwordField != null)
    {
      passwordField = null;
    }
    if (confirmButton != null)
    {
      confirmButton.clicked -= OnConfirmClicked;
      confirmButton = null;
    }
    if (closeButton != null)
    {
      closeButton.clicked -= OnCloseClicked;
      closeButton = null;
    }
    if (root != null)
    {
      root.UnregisterAllRemovableCallbacks();
    }
    if (panelRenderer != null)
      panelRenderer.UnregisterUIReloadCallback(OnUIReload);
  }

  void OnUIReload(PanelRenderer renderer, VisualElement root, int version)
  {
    if (uiVersion == version) return;
    uiVersion = version;

    this.root = root;

    passwordField = root.Q<TextField>("password-field");
    confirmButton = root.Q<Button>("confirm-button");
    closeButton = root.Q<Button>("close-button");

    confirmButton.clicked += OnConfirmClicked;
    closeButton.clicked += OnCloseClicked;

    root.RegisterCallback<NavigationSubmitEvent>(OnSubmit);
    root.RegisterCallback<NavigationCancelEvent>(OnCancel);

    Hide();
  }

  void OnSubmit(NavigationSubmitEvent evt)
  {
    OnConfirmClicked();
    evt.StopPropagation();
  }
  void OnCancel(NavigationCancelEvent evt)
  {
    Hide();
    evt.StopPropagation();
  }

  void OnCloseClicked()
  {
    Hide();
  }

  void OnConfirmClicked()
  {
    string password = passwordField.value;

    if (password.ToLower() == answer.ToLower())
    {
      OnPasswordConfirmed.Invoke();
      Hide();
    }
    else
    {
      OnPasswordRejected.Invoke();
      passwordField.value = "";
      passwordField.Focus();
    }
  }

  public void Show()
  {
    inputActions.FindActionMap("Player").Disable();
    UnityEngine.Cursor.lockState = CursorLockMode.None;
    UnityEngine.Cursor.visible = true;

    root.style.display = DisplayStyle.Flex;

    passwordField.value = "";
  }
  public void Hide()
  {
    root.style.display = DisplayStyle.None;
    passwordField.value = "";

    inputActions.FindActionMap("Player").Enable();
    UnityEngine.Cursor.lockState = CursorLockMode.Locked;
    UnityEngine.Cursor.visible = false;
  }
}