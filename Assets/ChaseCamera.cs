using UnityEngine;

/// <summary>
/// Chase camera with weight.
///
/// The camera stops being bolted to the plane and starts hanging behind it on a
/// spring, so throwing the plane around throws the camera around a beat later. It
/// falls back when you open the throttle, swings wide on the outside of a turn, and
/// settles once you fly straight again. None of this changes where the plane goes,
/// only how it reads.
///
/// Four things do most of the work:
///
///   Follow spring   The camera chases a point behind the plane instead of sitting on
///                   it. Under-damp that spring and it overshoots a little on every
///                   hard input, which is where the sense of mass comes from.
///   Partial roll    It copies only a fraction of the bank. Copying all of it spins the
///                   horizon and makes turns unreadable; copying none of it makes the
///                   plane look like it is sliding sideways through the air.
///   G swing         The plane's own acceleration shoves the camera the opposite way,
///                   the same way your head goes right when the car goes left.
///   Speed dolly     Distance and field of view open up with airspeed, which is what
///                   actually sells speed on a 160x144 screen where nothing else can.
///
/// Drop it on the Main Camera. It unparents itself at startup, because a child
/// transform is rigidly welded to its parent and none of the above can happen.
/// </summary>
[RequireComponent(typeof(Camera))]
public class ChaseCamera : MonoBehaviour
{
    [Header("Target")]
    [Tooltip("The plane to follow. Found automatically if left empty.")]
    [SerializeField] private Transform target;
    [Tooltip("On: the camera leaves the plane's hierarchy at startup so it can lag behind it. Turn this off only if the camera is already a root object.")]
    [SerializeField] private bool detachFromTarget = true;

    [Header("Rig")]
    [Tooltip("How far behind the plane the camera sits at rest.")]
    [SerializeField] private float followDistance = 7f;
    [Tooltip("How far above it. A little height is what keeps the ground in frame.")]
    [SerializeField] private float followHeight = 2.2f;
    [Tooltip("How far up the nose the camera aims. Larger puts the plane lower in frame and shows more of where you are going.")]
    [SerializeField] private float lookAhead = 25f;

    [Header("Follow spring")]
    [Tooltip("How many times a second the camera would bounce on its spring. Higher is tighter and more arcade, lower is heavier and more cinematic.")]
    [SerializeField] private float followFrequency = 1.6f;
    [Tooltip("1 settles without overshooting. Below 1 swings past and comes back, which is most of the character. Below about 0.4 gets seasick.")]
    [SerializeField, Range(0.2f, 1.5f)] private float followDamping = 0.7f;
    [Tooltip("How quickly the rig swings round to point the way the plane points. Lower drags the turn out behind you.")]
    [SerializeField] private float turnFollow = 6f;

    [Header("Aim")]
    [Tooltip("Spring on the point the camera looks at, in bounces per second.")]
    [SerializeField] private float aimFrequency = 3f;
    [SerializeField, Range(0.2f, 1.5f)] private float aimDamping = 1f;
    [Tooltip("How much the camera actually turns to keep that point framed. 0 stares straight down the rig, 1 tracks the nose exactly. Anything less than 1 lets the plane drift off centre under g, which is the point.")]
    [SerializeField, Range(0f, 1f)] private float aimTracking = 0.75f;

    [Header("Roll")]
    [Tooltip("Fraction of the plane's bank the camera copies. 0 keeps the horizon flat, 1 rolls with the wings. Around a half reads as banking without spinning the screen.")]
    [SerializeField, Range(0f, 1f)] private float rollFollow = 0.45f;

    [Header("Speed")]
    [Tooltip("The airspeed the gains below are measured against. Set it near cruise.")]
    [SerializeField] private float referenceSpeed = 25f;
    [Tooltip("Extra metres the camera pulls back at reference speed.")]
    [SerializeField] private float distanceGain = 3f;
    [Tooltip("Field of view when standing still. Leave at 0 to use whatever the Camera component is already set to.")]
    [SerializeField] private float baseFov = 0f;
    [Tooltip("Degrees of extra field of view at reference speed. This is the single biggest speed cue there is.")]
    [SerializeField] private float fovGain = 12f;
    [Tooltip("How fast the field of view chases the speed. Keep it slow, or throttle changes feel like a zoom lens.")]
    [SerializeField] private float fovFollow = 2f;

    [Header("G swing")]
    [Tooltip("Metres the camera is thrown per unit of the plane's acceleration, opposite the way it accelerates.")]
    [SerializeField] private float swingGain = 0.09f;
    [Tooltip("Ceiling on that throw, so a collision cannot fling the camera across the map.")]
    [SerializeField] private float maxSwing = 2.5f;
    [Tooltip("How fast the swing follows the g-load. Lower is looser and laggier.")]
    [SerializeField] private float swingFollow = 4f;
    [Tooltip("Smoothing on the measured acceleration. Physics steps are noisy and this is what stops the swing buzzing.")]
    [SerializeField] private float accelerationSmoothing = 10f;

    [Header("Shake")]
    [Tooltip("Degrees of rotational shake at full trauma. At 160x144 a degree is only about three pixels, so this needs to be bigger than it sounds.")]
    [SerializeField] private float shakeAngle = 2.2f;
    [Tooltip("Metres of positional shake at full trauma. Keep it small: rotation reads as impact, translation reads as nausea.")]
    [SerializeField] private float shakeOffset = 0.2f;
    [Tooltip("Wobbles per second. Higher is a sharp rattle, lower is a heavy lurch.")]
    [SerializeField] private float shakeFrequency = 20f;
    [Tooltip("How much trauma drains away per second. Shake is trauma squared, so the tail end fades fast on its own.")]
    [SerializeField] private float traumaFalloff = 1.6f;
    [Tooltip("Trauma held for as long as the wing is stalled, so losing the air over the wings is something you feel rather than only read on the gauge.")]
    [SerializeField, Range(0f, 1f)] private float stallShake = 0.5f;
    [Tooltip("Acceleration above which a hit counts as an impact. A hard turn is about 30, so anything well clear of that only fires on a real collision.")]
    [SerializeField] private float impactThreshold = 60f;
    [Tooltip("Trauma a full-force impact adds.")]
    [SerializeField, Range(0f, 1f)] private float impactShake = 0.8f;

    /// <summary>The plane this camera is following. Anything wanting to shake the screen can check it is the one being watched.</summary>
    public Transform Target => target;

    /// <summary>
    /// Kick the screen. 0 is nothing and 1 is the hardest shake the settings allow;
    /// think 0.05 for a gunshot, 0.3 for a hit taken, 1 for hitting the ground.
    /// Kicks stack, so a burst of fire builds up rather than resetting on every shot.
    /// </summary>
    public void AddTrauma(float amount) => trauma = Mathf.Clamp01(trauma + amount);

    private new Camera camera;
    private Rigidbody targetBody;
    private AirshipController plane;

    private Vector3 rigPosition;
    private Vector3 rigVelocity;
    private Quaternion rigRotation = Quaternion.identity;

    private Vector3 aimPoint;
    private Vector3 aimVelocity;

    private Vector3 swing;
    private Vector3 smoothedAcceleration;
    private Vector3 lastTargetVelocity;

    private float fov;
    private float trauma;
    private float lastBank;
    private float noiseSeed;

    private void Awake()
    {
        camera = GetComponent<Camera>();

        if (target == null)
        {
            AirshipController found = FindFirstObjectByType<AirshipController>();
            if (found != null)
                target = found.transform;
        }

        if (target != null)
        {
            targetBody = target.GetComponent<Rigidbody>();
            plane = target.GetComponent<AirshipController>();
        }

        // A child transform inherits its parent's rotation exactly, which is the whole
        // problem: no spring can lag behind something it is welded to.
        if (detachFromTarget && transform.parent != null)
            transform.SetParent(null, true);

        if (baseFov <= 0f)
            baseFov = camera.fieldOfView;

        fov = baseFov;
        noiseSeed = Random.value * 1000f;
    }

    private void Start() => SnapToTarget();

    /// <summary>
    /// Drop the camera straight into its resting position with no spring, no lag and
    /// no shake. Call this after a respawn or a teleport, or the camera will come
    /// screaming across the level to catch up.
    /// </summary>
    public void SnapToTarget()
    {
        if (target == null)
            return;

        rigRotation = RestingRotation();
        rigPosition = target.position + rigRotation * new Vector3(0f, followHeight, -followDistance);
        aimPoint = target.position + target.forward * lookAhead;

        rigVelocity = Vector3.zero;
        aimVelocity = Vector3.zero;
        swing = Vector3.zero;
        smoothedAcceleration = Vector3.zero;
        lastTargetVelocity = TargetVelocity();
        trauma = 0f;

        transform.SetPositionAndRotation(rigPosition, rigRotation);
    }

    private void LateUpdate()
    {
        if (target == null)
            return;

        // Clamped so a loading hitch cannot blow the springs up, and because a camera
        // that catches up instantly across a one second stall looks worse than one that
        // takes an extra frame about it.
        float dt = Mathf.Min(Time.deltaTime, 1f / 30f);
        if (dt <= 0f)
            return;

        float speedRatio = TargetVelocity().magnitude / Mathf.Max(referenceSpeed, 0.01f);

        UpdateRotation(dt);
        UpdatePosition(speedRatio, dt);
        UpdateFieldOfView(speedRatio, dt);

        ApplyShake(AimRotation(dt), dt);
    }

    /// <summary>
    /// The g-load, which feeds both the swing and the impact detector. Measured here
    /// rather than up in LateUpdate because velocity only ever changes on a physics
    /// step: sampling it per rendered frame divides one step's worth of change by a
    /// much shorter frame time, and reports several times the real acceleration the
    /// moment the game draws faster than it simulates.
    /// </summary>
    private void FixedUpdate()
    {
        if (target == null)
            return;

        float dt = Time.fixedDeltaTime;
        Vector3 velocity = TargetVelocity();
        Vector3 acceleration = (velocity - lastTargetVelocity) / dt;
        lastTargetVelocity = velocity;

        // Checked before smoothing, or the spike is averaged away before anyone sees it.
        // A hard turn is worth a couple of g, so a threshold well clear of that only
        // trips on something that really was a wall.
        float magnitude = acceleration.magnitude;
        if (magnitude > impactThreshold)
            AddTrauma(Mathf.InverseLerp(impactThreshold, impactThreshold * 3f, magnitude) * impactShake);

        smoothedAcceleration = Vector3.Lerp(smoothedAcceleration, acceleration, Blend(accelerationSmoothing, dt));
    }

    // --- The rig -----------------------------------------------------------

    private void UpdateRotation(float dt)
    {
        rigRotation = Quaternion.Slerp(rigRotation, RestingRotation(), Blend(turnFollow, dt));
    }

    /// <summary>Where the rig wants to point: down the nose, with part of the bank taken back out.</summary>
    private Quaternion RestingRotation()
    {
        float bank = BankAngle();
        return target.rotation * Quaternion.AngleAxis(-bank * (1f - rollFollow), Vector3.forward);
    }

    /// <summary>
    /// How far the wings are rolled from level, in degrees, measured around the nose.
    /// It has no meaning when the nose points straight up or down, where rolling and
    /// yawing are the same motion, so there we hold the last reading rather than let
    /// the camera snap through a loop.
    /// </summary>
    private float BankAngle()
    {
        Vector3 forward = target.forward;
        Vector3 levelUp = Vector3.ProjectOnPlane(Vector3.up, forward);

        if (levelUp.sqrMagnitude > 0.001f)
            lastBank = Vector3.SignedAngle(levelUp.normalized, target.up, forward);

        return lastBank;
    }

    private void UpdatePosition(float speedRatio, float dt)
    {
        // Faster means further back, which widens the frame exactly when you need more
        // warning about what is coming.
        float distance = followDistance + speedRatio * distanceGain;

        // The plane's acceleration read in the camera's own axes, then pushed back the
        // other way: pull g in a left turn and the camera slides out to the right.
        Vector3 localAcceleration = Quaternion.Inverse(rigRotation) * smoothedAcceleration;
        Vector3 swingGoal = Vector3.ClampMagnitude(-localAcceleration * swingGain, maxSwing);
        swing = Vector3.Lerp(swing, swingGoal, Blend(swingFollow, dt));

        Vector3 anchor = target.position + rigRotation * (new Vector3(0f, followHeight, -distance) + swing);
        Spring(ref rigPosition, ref rigVelocity, anchor, followFrequency, followDamping, dt);
    }

    private Quaternion AimRotation(float dt)
    {
        // A point out in front of the nose on its own spring, so the camera keeps
        // looking where the plane was pointing for a moment after it stops pointing there.
        Vector3 aimGoal = target.position + target.forward * lookAhead;
        Spring(ref aimPoint, ref aimVelocity, aimGoal, aimFrequency, aimDamping, dt);

        Vector3 toAim = aimPoint - rigPosition;
        if (toAim.sqrMagnitude < 0.001f)
            return rigRotation;

        // Blended rather than used outright: tracking the aim point exactly pins the
        // plane to the middle of the screen and throws away everything the springs did.
        Quaternion look = Quaternion.LookRotation(toAim, rigRotation * Vector3.up);
        return Quaternion.Slerp(rigRotation, look, aimTracking);
    }

    private void UpdateFieldOfView(float speedRatio, float dt)
    {
        fov = Mathf.Lerp(fov, baseFov + speedRatio * fovGain, Blend(fovFollow, dt));
        camera.fieldOfView = fov;
    }

    // --- Shake -------------------------------------------------------------

    private void ApplyShake(Quaternion rotation, float dt)
    {
        trauma = Mathf.Max(0f, trauma - traumaFalloff * dt);

        // A floor rather than something added each frame: a stall lasts as long as it
        // lasts, and trickling trauma in would just lose a race against the falloff.
        // Applied after the decay, so a real hit still fades back down to this.
        if (plane != null && plane.IsStalling)
            trauma = Mathf.Max(trauma, stallShake);

        // Squared, so shake ramps up hard at the top and then gets out of the way
        // instead of trailing off into a long buzz.
        float shake = trauma * trauma;
        if (shake <= 0f)
        {
            transform.SetPositionAndRotation(rigPosition, rotation);
            return;
        }

        // Perlin rather than Random, so one frame is related to the next and the result
        // is a wobble instead of static. Separate lanes through the noise field keep the
        // axes from all moving as one.
        float t = Time.time * shakeFrequency;
        float pitch = Noise(0f, t) * shakeAngle * shake;
        float yaw = Noise(1f, t) * shakeAngle * shake;
        float roll = Noise(2f, t) * shakeAngle * shake;

        Quaternion shaken = rotation * Quaternion.Euler(pitch, yaw, roll);
        Vector3 offset = shaken * new Vector3(Noise(3f, t), Noise(4f, t), 0f) * (shakeOffset * shake);

        transform.SetPositionAndRotation(rigPosition + offset, shaken);
    }

    private float Noise(float lane, float t) => Mathf.PerlinNoise(noiseSeed + lane * 100f, t) * 2f - 1f;

    // --- Helpers -----------------------------------------------------------

    private Vector3 TargetVelocity() => targetBody != null ? targetBody.linearVelocity : Vector3.zero;

    /// <summary>
    /// One step of a damped spring, written as a frequency and a damping ratio rather
    /// than a raw stiffness, because those two mean something you can picture:
    /// frequency is how many times a second it would bounce, and ratio 1 is the fastest
    /// it can settle without going past what it is chasing.
    /// </summary>
    private static void Spring(ref Vector3 value, ref Vector3 velocity, Vector3 goal, float frequency, float damping, float dt)
    {
        float omega = 2f * Mathf.PI * Mathf.Max(frequency, 0.01f);
        Vector3 acceleration = (goal - value) * (omega * omega) - velocity * (2f * damping * omega);

        // Velocity first, then position from the new velocity. That is the semi-implicit
        // form, which stays stable at frame times where the naive one blows up.
        velocity += acceleration * dt;
        value += velocity * dt;
    }

    /// <summary>
    /// Lerp factor for "close this much of the gap per second", built so it gives the
    /// same motion at 30 fps and at 144. A bare Lerp(a, b, rate * dt) does not.
    /// </summary>
    private static float Blend(float rate, float dt) => 1f - Mathf.Exp(-rate * dt);
}
