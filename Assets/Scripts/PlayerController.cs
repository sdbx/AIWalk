using UnityEngine;
using UnityEngine.InputSystem;

[RequireComponent(typeof(Rigidbody))]
public class PlayerController : MonoBehaviour
{
  [Header("Movement Settings")]
  [SerializeField] private float moveSpeed = 8f;
  [SerializeField] private float jumpForce = 5f;

  [Header("Look Settings")]
  [SerializeField] private Transform playerCamera;
  [SerializeField] private float mouseSensitivity = 0.1f;
  [SerializeField] private float topLookLimit = -80f;
  [SerializeField] private float bottomLookLimit = 80f;

  private Rigidbody rb;
  private Vector2 moveInput;
  private Vector2 lookInput;
  private float verticalRotation = 0f;
  private bool isGrounded;

  void Awake()
  {
    rb = GetComponent<Rigidbody>();
    rb.freezeRotation = true;
  }

  void Start()
  {
    Cursor.lockState = CursorLockMode.Locked;
    Cursor.visible = false;
  }

  void Update()
  {
    isGrounded = Physics.Raycast(transform.position, Vector3.down, 1.1f);

    HandleLook();
  }

  void FixedUpdate()
  {
    MovePlayer();
  }

  public void OnMove(InputAction.CallbackContext context)
  {
    moveInput = context.ReadValue<Vector2>();
  }
  public void OnLook(InputAction.CallbackContext context)
  {
    lookInput = context.ReadValue<Vector2>();
  }
  public void OnJump(InputAction.CallbackContext context)
  {
    if (context.started && isGrounded)
    {
      rb.AddForce(Vector3.up * jumpForce, ForceMode.Impulse);
    }
  }

  private void HandleLook()
  {
    transform.Rotate(lookInput.x * mouseSensitivity * Vector3.up);

    verticalRotation -= lookInput.y * mouseSensitivity;
    verticalRotation = Mathf.Clamp(verticalRotation,topLookLimit, bottomLookLimit);
    playerCamera.localRotation = Quaternion.Euler(verticalRotation, 0f, 0f);
  }

  private void MovePlayer()
  {
    Vector3 moveDir = transform.right * moveInput.x + transform.forward * moveInput.y;
    Vector3 targetVelocity = new(moveDir.x * moveSpeed, rb.linearVelocity.y, moveDir.z * moveSpeed);
    rb.linearVelocity = targetVelocity;
  }
}
