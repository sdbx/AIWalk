using UnityEngine;
using UnityEngine.InputSystem;

public class RobotArmsController : MonoBehaviour
{
  [Header("Bones")]
  [SerializeField] private Transform upperArmL;
  [SerializeField] private Transform upperArmR;
  [SerializeField] private Transform forearmL;
  [SerializeField] private Transform forearmR;
  [SerializeField] private Transform handL;
  [SerializeField] private Transform handR;

  [Header("Rig")]
  [SerializeField] private Transform rigRoot;
  [SerializeField] private float referenceSpeed = 8f;
  [SerializeField] private float blendSpeed = 10f;

  [Header("Idle")]
  [SerializeField] private float idleFrequency = 1.6f;
  [SerializeField] private float idleBob = 0.006f;
  [SerializeField] private float idlePitch = 1.2f;

  [Header("Walk")]
  [SerializeField] private float walkFrequency = 1.8f;
  [SerializeField] private float walkBob = 0.02f;
  [SerializeField] private float walkSide = 0.012f;
  [SerializeField] private float walkRoll = 2.5f;
  [SerializeField] private float walkPitch = 1.5f;

  [Header("Look Sway")]
  [SerializeField] private float swayYaw = 0.25f;
  [SerializeField] private float swayPitch = 0.2f;
  [SerializeField] private float swayMax = 7f;
  [SerializeField] private float swaySmooth = 7f;

  [Header("Interact")]
  [SerializeField] private float interactDuration = 0.5f;
  [SerializeField] private float interactPush = 0.09f;
  [SerializeField] private float interactArmPitch = -10f;
  [SerializeField] private float interactForearmPitch = 16f;
  [SerializeField] private float interactHandPitch = 26f;

  [Header("Jump / Land")]
  [SerializeField] private float jumpDip = 0.05f;
  [SerializeField] private float jumpPitch = 7f;
  [SerializeField] private float jumpDuration = 0.3f;
  [SerializeField] private float airLag = 0.006f;
  [SerializeField] private float airLagMax = 0.045f;
  [SerializeField] private float landDip = 0.09f;
  [SerializeField] private float landPitch = 12f;
  [SerializeField] private float landHandPitch = 18f;
  [SerializeField] private float landDuration = 0.45f;
  [SerializeField] private float referenceImpactSpeed = 7f;

  private Transform cameraTransform;
  private Rigidbody body;
  private PlayerInput playerInput;
  private PlayerController playerController;
  private InputAction interactAction;

  private Vector3 restPosition;
  private Quaternion restRotation;
  private Quaternion upperArmLRest;
  private Quaternion upperArmRRest;
  private Quaternion forearmLRest;
  private Quaternion forearmRRest;
  private Quaternion handLRest;
  private Quaternion handRRest;

  private float lastYaw;
  private float lastPitch;
  private bool swayInitialized;
  private Vector2 swayOffset;
  private float interactStart = float.NegativeInfinity;
  private float jumpStart = float.NegativeInfinity;
  private float landStart = float.NegativeInfinity;
  private float landStrength;

  public bool IsInteracting => Time.time - interactStart < interactDuration;

  void Awake()
  {
    if (rigRoot == null)
      rigRoot = transform;

    if (upperArmL == null)
      upperArmL = FindBone("UpperArm_L");
    if (upperArmR == null)
      upperArmR = FindBone("UpperArm_R");
    if (forearmL == null)
      forearmL = FindBone("Forearm_L");
    if (forearmR == null)
      forearmR = FindBone("Forearm_R");
    if (handL == null)
      handL = FindBone("Hand_L");
    if (handR == null)
      handR = FindBone("Hand_R");

    Camera playerCamera = GetComponentInParent<Camera>();
    cameraTransform = playerCamera != null ? playerCamera.transform : null;
    body = GetComponentInParent<Rigidbody>();
    playerInput = GetComponentInParent<PlayerInput>();
    playerController = GetComponentInParent<PlayerController>();

    restPosition = rigRoot.localPosition;
    restRotation = rigRoot.localRotation;
    upperArmLRest = ReadRest(upperArmL);
    upperArmRRest = ReadRest(upperArmR);
    forearmLRest = ReadRest(forearmL);
    forearmRRest = ReadRest(forearmR);
    handLRest = ReadRest(handL);
    handRRest = ReadRest(handR);
  }

  void OnEnable()
  {
    if (playerController != null)
    {
      playerController.Jumped += HandleJump;
      playerController.Landed += HandleLanded;
    }

    if (playerInput == null || playerInput.actions == null)
      return;

    interactAction = playerInput.actions.FindAction("Interact", false);
    if (interactAction != null)
      interactAction.started += HandleInteract;
  }

  void OnDisable()
  {
    if (playerController != null)
    {
      playerController.Jumped -= HandleJump;
      playerController.Landed -= HandleLanded;
    }

    if (interactAction != null)
      interactAction.started -= HandleInteract;
  }

  void LateUpdate()
  {
    float dt = Time.deltaTime;
    if (dt <= 0f || rigRoot == null)
      return;

    UpdateSway(dt);

    float speed = 0f;
    if (body != null)
    {
      Vector3 velocity = body.linearVelocity;
      speed = new Vector2(velocity.x, velocity.z).magnitude;
    }

    float walk = Mathf.Clamp01(speed / Mathf.Max(referenceSpeed, 0.01f));
    float phase = Time.time * walkFrequency * Mathf.PI * 2f;
    float idlePhase = Time.time * idleFrequency * Mathf.PI * 2f;
    float interact = Impulse(interactStart, interactDuration);
    float jump = Impulse(jumpStart, jumpDuration);
    float land = landStrength * Impulse(landStart, landDuration);
    float blend = 1f - Mathf.Exp(-blendSpeed * dt);

    float vertical = -jumpDip * jump - landDip * land;
    if (playerController != null && !playerController.IsGrounded && body != null)
      vertical += Mathf.Clamp(-body.linearVelocity.y * airLag, -airLagMax, airLagMax);

    Vector3 bob = new Vector3(
      Mathf.Cos(phase * 0.5f) * walkSide * walk,
      Mathf.Sin(phase) * walkBob * walk + Mathf.Sin(idlePhase) * idleBob + vertical,
      interactPush * interact);

    Quaternion rootTarget = Quaternion.Euler(
      swayOffset.y + Mathf.Sin(phase) * walkPitch * walk + jumpPitch * jump + landPitch * land,
      swayOffset.x,
      -swayOffset.x * 0.4f + Mathf.Cos(phase) * walkRoll * walk);

    rigRoot.localPosition = Vector3.Lerp(rigRoot.localPosition, restPosition + bob, blend);
    rigRoot.localRotation = Quaternion.Slerp(rigRoot.localRotation, restRotation * rootTarget, blend);

    Quaternion idleBone = Quaternion.Euler(Mathf.Sin(idlePhase) * idlePitch, 0f, 0f);
    Quaternion walkBone = Quaternion.Euler(Mathf.Sin(phase) * walkPitch * -0.5f * walk, 0f, Mathf.Cos(phase) * walkRoll * walk);
    Quaternion interactArm = Quaternion.Euler(interact * interactArmPitch, 0f, 0f);
    Quaternion interactForearm = Quaternion.Euler(interact * interactForearmPitch, 0f, 0f);
    Quaternion interactHand = Quaternion.Euler(interact * interactHandPitch + landHandPitch * land, 0f, 0f);

    BlendBone(upperArmL, upperArmLRest, idleBone, walkBone, interactArm, blend);
    BlendBone(upperArmR, upperArmRRest, idleBone, walkBone, interactArm, blend);
    BlendBone(forearmL, forearmLRest, Quaternion.identity, walkBone, interactForearm, blend);
    BlendBone(forearmR, forearmRRest, Quaternion.identity, walkBone, interactForearm, blend);
    BlendBone(handL, handLRest, Quaternion.identity, Quaternion.identity, interactHand, blend);
    BlendBone(handR, handRRest, Quaternion.identity, Quaternion.identity, interactHand, blend);
  }

  public void PlayInteract()
  {
    if (IsInteracting)
      return;

    interactStart = Time.time;
  }

  private void HandleInteract(InputAction.CallbackContext context)
  {
    PlayInteract();
  }

  private void HandleJump()
  {
    jumpStart = Time.time;
  }

  private void HandleLanded(float impactSpeed)
  {
    landStart = Time.time;
    landStrength = Mathf.Clamp01(impactSpeed / Mathf.Max(referenceImpactSpeed, 0.01f));
  }

  private void UpdateSway(float dt)
  {
    if (cameraTransform == null)
    {
      swayOffset = Vector2.Lerp(swayOffset, Vector2.zero, 1f - Mathf.Exp(-swaySmooth * dt));
      return;
    }

    Vector3 euler = cameraTransform.eulerAngles;
    float yaw = euler.y;
    float pitch = euler.x > 180f ? euler.x - 360f : euler.x;

    if (swayInitialized)
    {
      float yawVelocity = Mathf.DeltaAngle(lastYaw, yaw) / dt;
      float pitchVelocity = Mathf.DeltaAngle(lastPitch, pitch) / dt;
      Vector2 target = new Vector2(
        Mathf.Clamp(-yawVelocity * swayYaw, -swayMax, swayMax),
        Mathf.Clamp(-pitchVelocity * swayPitch, -swayMax, swayMax));
      swayOffset = Vector2.Lerp(swayOffset, target, 1f - Mathf.Exp(-swaySmooth * dt));
    }

    lastYaw = yaw;
    lastPitch = pitch;
    swayInitialized = true;
  }

  private float Impulse(float startTime, float duration)
  {
    float t = (Time.time - startTime) / Mathf.Max(duration, 0.01f);
    if (t < 0f || t > 1f)
      return 0f;

    return Mathf.Sin(t * Mathf.PI);
  }

  private void BlendBone(Transform bone, Quaternion rest, Quaternion idle, Quaternion walk, Quaternion interact, float blend)
  {
    if (bone == null)
      return;

    Quaternion target = rest * idle * walk * interact;
    bone.localRotation = Quaternion.Slerp(bone.localRotation, target, blend);
  }

  private Quaternion ReadRest(Transform bone)
  {
    return bone != null ? bone.localRotation : Quaternion.identity;
  }

  private Transform FindBone(string boneName)
  {
    foreach (Transform candidate in GetComponentsInChildren<Transform>())
    {
      if (candidate.name == boneName)
        return candidate;
    }

    Debug.LogWarning($"RobotArmsController: bone '{boneName}' not found.", this);
    return null;
  }
}
