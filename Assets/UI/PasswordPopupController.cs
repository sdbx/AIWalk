using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UIElements;

[RequireComponent(typeof(PanelRenderer))]
public class PasswordPopupController : MonoBehaviour
{
  public UnityEvent<string> OnPasswordSubmit;

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
    OnPasswordSubmit.Invoke(passwordField.value.Trim());
  }

  public void ClearAndFocus()
  {
      passwordField.value = "";
      passwordField.Focus();
  }

  public void Show()
  {
    CursorManager.Instance.SetUIMode();

    root.style.display = DisplayStyle.Flex;

    passwordField.value = "";
  }
  public void Hide()
  {
    root.style.display = DisplayStyle.None;
    passwordField.value = "";

    CursorManager.Instance?.SetFpsMode();
  }
}