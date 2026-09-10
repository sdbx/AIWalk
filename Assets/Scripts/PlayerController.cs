using UnityEngine;
using UnityEngine.InputSystem;

[RequireComponent(typeof(Rigidbody))]
public class PlayerController : MonoBehaviour
{
  [Header("Movement Settings")]
  [SerializeField] private float moveSpeed = 8f;

  [Header("Jump Settings")]
  [SerializeField] private float jumpForce = 5f;
  [SerializeField] private float coyoteTime = 0.1f;
  [SerializeField] private float jumpBufferTime = 0.1f;

  [Header("Look Settings")]
  [SerializeField] private Transform playerCamera;
  [SerializeField] private float mouseSensitivity = 0.1f;
  [SerializeField] private float gamepadSensitivity = 120f;
  [SerializeField] private float minLookAngle = -80f;
  [SerializeField] private float maxLookAngle = 80f;

  [Header("Ground Check")]
  [SerializeField] private LayerMask groundMask = ~0;
  [SerializeField] private float groundCheckRadius = 0.4f;
  [SerializeField] private float groundCheckOffset = 0.05f;

  private const int GroundOverlapCapacity = 8;

  private Rigidbody rb;
  private Collider bodyCollider;
  private readonly Collider[] groundOverlaps = new Collider[GroundOverlapCapacity];

  private Vector2 moveInput;
  private Vector2 lookInput;
  private float verticalRotation;
  private bool isGrounded;
  private float jumpRequestedTime = float.NegativeInfinity;
  private float lastGroundedTime = float.NegativeInfinity;

  void Awake()
  {
    rb = GetComponent<Rigidbody>();
    rb.freezeRotation = true;
    rb.interpolation = RigidbodyInterpolation.Interpolate;

    bodyCollider = GetComponentInChildren<Collider>();

    if (playerCamera == null)
    {
      Debug.LogError("PlayerController: playerCamera is not assigned.", this);
      return;
    }

    float pitch = playerCamera.localEulerAngles.x;
    verticalRotation = pitch > 180f ? pitch - 360f : pitch;
  }

  void Start()
  {
    Cursor.lockState = CursorLockMode.Locked;
    Cursor.visible = false;
  }

  void Update()
  {
    HandleLook();
  }

  void FixedUpdate()
  {
    UpdateGroundedState();
    HandleJump();
    MovePlayer();
  }

  public void OnMove(InputAction.CallbackContext context)
  {
    moveInput = context.ReadValue<Vector2>();
  }

  public void OnLook(InputAction.CallbackContext context)
  {
    Vector2 input = context.ReadValue<Vector2>();

    if (context.control.device is Pointer)
      lookInput = input * mouseSensitivity;
    else
      lookInput = input * gamepadSensitivity * Time.deltaTime;
  }

  public void OnJump(InputAction.CallbackContext context)
  {
    if (context.started)
      jumpRequestedTime = Time.time;
  }

  private void HandleLook()
  {
    if (playerCamera == null)
      return;

    if (lookInput.x != 0f)
    {
      transform.Rotate(0f, lookInput.x, 0f, Space.Self);
      Physics.SyncTransforms();
    }

    verticalRotation = Mathf.Clamp(verticalRotation - lookInput.y, minLookAngle, maxLookAngle);
    playerCamera.localRotation = Quaternion.Euler(verticalRotation, 0f, 0f);
  }

  private void UpdateGroundedState()
  {
    int mask = groundMask.value == 0 ? Physics.AllLayers : groundMask.value;
    int count = Physics.OverlapSphereNonAlloc(GetGroundCheckPosition(), groundCheckRadius, groundOverlaps, mask, QueryTriggerInteraction.Ignore);

    isGrounded = false;
    for (int i = 0; i < count; i++)
    {
      Transform overlap = groundOverlaps[i].transform;
      if (overlap == transform || overlap.IsChildOf(transform))
        continue;

      isGrounded = true;
      break;
    }

    if (isGrounded)
      lastGroundedTime = Time.time;
  }

  private Vector3 GetGroundCheckPosition()
  {
    if (bodyCollider == null)
      return transform.position + Vector3.down * (1f - groundCheckRadius + groundCheckOffset);

    Bounds bounds = bodyCollider.bounds;
    Vector3 feet = bounds.center - Vector3.up * bounds.extents.y;
    return feet + Vector3.up * (groundCheckRadius - groundCheckOffset);
  }

  private void HandleJump()
  {
    if (Time.time - jumpRequestedTime > jumpBufferTime)
      return;
    if (Time.time - lastGroundedTime > coyoteTime)
      return;

    jumpRequestedTime = float.NegativeInfinity;
    lastGroundedTime = float.NegativeInfinity;
    rb.AddForce(Vector3.up * jumpForce, ForceMode.Impulse);
  }

  private void MovePlayer()
  {
    Vector2 input = Vector2.ClampMagnitude(moveInput, 1f);
    Vector3 moveDir = transform.right * input.x + transform.forward * input.y;
    Vector3 targetVelocity = new Vector3(moveDir.x * moveSpeed, rb.linearVelocity.y, moveDir.z * moveSpeed);
    rb.linearVelocity = targetVelocity;
  }
}
