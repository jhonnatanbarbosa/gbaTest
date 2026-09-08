using UnityEngine;

/// <summary>
/// Chase camera with weight, built to read the way GTA V's aircraft camera reads.
///
/// The camera stops being bolted to the plane and starts hanging behind it on a
/// spring, so throwing the plane around throws the camera around a beat later. None
/// of this changes where the plane goes, only how it looks.
///
/// Five things do most of the work:
///
///   Swing follow    The rig turns onto the nose by the shortest rotation there is,
///                   never through a fixed up-axis. That has no poles in it, so loops
///                   and vertical climbs cost nothing and snap nowhere.
///   Rolling horizon How far the camera lies in the wings rather than on the horizon,
///                   set by rollFollow and rollMatch. Matched, the world goes over with
///                   you and inverted reads as inverted. Turned down, the camera picks
///                   up a share of each roll and then rights itself over the next
///                   second or so, which is the GTA V compromise: a barrel roll feels
///                   connected, a held bank still leaves a horizon you can read.
///   G swing         What an accelerometer bolted to the plane would feel, shoved back
///                   the other way, the same as your head going right when the car goes
///                   left. Gravity is taken out of it first, so a dive is weightless
///                   and floats the camera up instead of throwing it.
///   Speed dolly     Distance and field of view open up with airspeed, which is what
///                   actually sells speed on a 160x144 screen where nothing else can.
///   Wall dodge      A sphere cast from the plane pulls the camera in rather than
///                   letting a building slide through the frame.
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
    [SerializeField] private float followDistance = 8f;
    [Tooltip("How far above it. A little height is what keeps the ground in frame.")]
    [SerializeField] private float followHeight = 2.4f;
    [Tooltip("How far up the nose the camera aims. Larger puts the plane lower in frame and shows more of where you are going.")]
    [SerializeField] private float lookAhead = 30f;
    [Tooltip("How far above the nose it aims. Positive sits the plane a little low in frame, which is where GTA V keeps it.")]
    [SerializeField] private float aimHeight = 0.6f;

    [Header("Follow")]
    [Tooltip("How quickly the rig swings round to point the way the plane points. Lower drags the turn out behind you.")]
    [SerializeField] private float turnFollow = 5f;
    [Tooltip("How much of the plane's flight path, rather than its nose, the rig points down. A little of this keeps a slipping plane framed against where it is actually going.")]
    [SerializeField, Range(0f, 1f)] private float flightPathBlend = 0.2f;
    [Tooltip("How many times a second the camera would bounce on its spring. Higher is tighter and more arcade, lower is heavier and more cinematic.")]
    [SerializeField] private float followFrequency = 2.2f;
    [Tooltip("1 settles without overshooting. Below 1 swings past and comes back, which is most of the character. Below about 0.4 gets seasick.")]
    [SerializeField, Range(0.2f, 1.5f)] private float followDamping = 0.85f;

    [Header("Aim")]
    [Tooltip("Spring on the point the camera looks at, in bounces per second.")]
    [SerializeField] private float aimFrequency = 3.5f;
    [SerializeField, Range(0.2f, 1.5f)] private float aimDamping = 1f;
    [Tooltip("How much the camera actually turns to keep that point framed. 0 stares straight down the rig, 1 tracks the nose exactly. Anything less than 1 lets the plane drift off centre under g, which is the point.")]
    [SerializeField, Range(0f, 1f)] private float aimTracking = 0.8f;

    [Header("Roll")]
    [Tooltip("How much the camera lies in the plane's own wings rather than on the horizon. 1 bolts the horizon to the wings: roll inverted and the world goes over with you, hold a knife edge and the camera holds it too. 0 is a self-righting horizon that only borrows a share of each roll. Anything in between rides partway over.")]
    [SerializeField, Range(0f, 1f)] private float rollMatch = 1f;
    [Tooltip("How fast the camera catches up to the wings, per second. Low enough to lag a snap roll by a beat, high enough not to feel loose.")]
    [SerializeField] private float rollMatchRate = 8f;
    [Tooltip("Share of every roll the plane makes that the camera picks up. Rolls are copied as they happen rather than as an angle to match, which is what makes this work upside down.")]
    [SerializeField, Range(0f, 1f)] private float rollFollow = 0.55f;
    [Tooltip("How fast the horizon rights itself again afterwards, per second. This is what stops a held bank leaving the whole screen tilted.")]
    [SerializeField] private float rollLevel = 1.6f;
    [Tooltip("Hard limit on how far the horizon is ever allowed to tip.")]
    [SerializeField] private float maxRoll = 55f;

    [Header("Speed")]
    [Tooltip("The airspeed the gains below are measured against, and where they stop growing. Set it near the plane's top speed.")]
    [SerializeField] private float referenceSpeed = 28f;
    [Tooltip("Extra metres the camera pulls back at reference speed.")]
    [SerializeField] private float distanceGain = 3f;
    [Tooltip("Field of view when standing still. Leave at 0 to use whatever the Camera component is already set to.")]
    [SerializeField] private float baseFov = 0f;
    [Tooltip("Degrees of extra field of view at reference speed. This is the single biggest speed cue there is.")]
    [SerializeField] private float fovGain = 14f;
    [Tooltip("How fast the field of view chases the speed. Keep it slow, or throttle changes feel like a zoom lens.")]
    [SerializeField] private float fovFollow = 2f;

    [Header("G swing")]
    [Tooltip("Metres the camera is thrown per unit of g-load, opposite the way the plane is accelerating.")]
    [SerializeField] private float swingGain = 0.1f;
    [Tooltip("Ceiling on that throw, so a collision cannot fling the camera across the map.")]
    [SerializeField] private float maxSwing = 2.5f;
    [Tooltip("How fast the swing follows the g-load. Lower is looser and laggier.")]
    [SerializeField] private float swingFollow = 5f;
    [Tooltip("Smoothing on the measured acceleration. Physics steps are noisy and this is what stops the swing buzzing.")]
    [SerializeField] private float accelerationSmoothing = 12f;

    [Header("Wall dodge")]
    [Tooltip("On: the camera is pulled in rather than allowed to sit inside a building. Worth its cost in any level you can fly between things in.")]
    [SerializeField] private bool avoidGeometry = true;
    [SerializeField] private LayerMask collisionMask = ~0;
    [Tooltip("How fat the probe is. Roughly the near plane, or the camera will still clip corners it squeezed past.")]
    [SerializeField] private float collisionRadius = 0.4f;
    [Tooltip("Closest the camera is ever pushed to the plane.")]
    [SerializeField] private float minDistance = 1.5f;
    [Tooltip("How fast it lets itself back out once the wall is gone. Snapping in is fine, snapping out is not.")]
    [SerializeField] private float pullOutRate = 3f;

    [Header("Look behind")]
    [Tooltip("Hold Select to swing round in front and look back down the tail, the way GTA V's look-behind works.")]
    [SerializeField] private bool selectLooksBehind = true;

    [Header("Shake")]
    [Tooltip("Degrees of rotational shake at full trauma. At 160x144 a degree is only about three pixels, so this needs to be bigger than it sounds.")]
    [SerializeField] private float shakeAngle = 2.2f;
    [Tooltip("Metres of positional shake at full trauma. Keep it small: rotation reads as impact, translation reads as nausea.")]
    [SerializeField] private float shakeOffset = 0.2f;
    [Tooltip("Wobbles per second. Higher is a sharp rattle, lower is a heavy lurch.")]
    [SerializeField] private float shakeFrequency = 20f;
    [Tooltip("How much trauma drains away per second. Shake is trauma squared, so the tail end fades fast on its own.")]
    [SerializeField] private float traumaFalloff = 1.6f;
    [Tooltip("Trauma held at a full stall, scaled by how deep the stall is, so losing the air over the wings is something you feel rather than only read on the gauge.")]
    [SerializeField, Range(0f, 1f)] private float stallShake = 0.5f;
    [Tooltip("G-load above which a hit counts as an impact. A hard turn is worth about three, so anything well clear of that only fires on a real collision.")]
    [SerializeField] private float impactThreshold = 60f;
    [Tooltip("Trauma a full-force impact adds.")]
    [SerializeField, Range(0f, 1f)] private float impactShake = 0.8f;

    /// <summary>The plane this camera is following. Anything wanting to shake the screen can check it is the one being watched.</summary>
    public Transform Target => target;

    /// <summary>True while the camera has swung round to look back down the tail.</summary>
    public bool LookingBehind { get; private set; }

    /// <summary>
    /// Kick the screen. 0 is nothing and 1 is the hardest shake the settings allow;
    /// think 0.05 for a gunshot, 0.3 for a hit taken, 1 for hitting the ground.
    /// Kicks stack, so a burst of fire builds up rather than resetting on every shot.
    /// </summary>
    public void AddTrauma(float amount) => trauma = Mathf.Clamp01(trauma + amount);

    private new Camera camera;
    private Rigidbody targetBody;
    private AirshipController plane;

    // The rig is an orientation plus an offset from the plane, never a world position.
    // A spring chasing a world position that is itself moving at 25 m/s never catches
    // it, and trails by an amount that grows with speed whether you wanted it to or not.
    private Quaternion rigRotation = Quaternion.identity;
    private Vector3 rigOffset;
    private Vector3 rigOffsetVelocity;
    private Vector3 rigPosition;

    private Vector3 aimOffset;
    private Vector3 aimOffsetVelocity;

    private Vector3 swing;
    private Vector3 smoothedForce;
    private Vector3 lastTargetVelocity;
    private Quaternion lastTargetRotation = Quaternion.identity;

    private float collisionDistance;
    private float fov;
    private float trauma;
    private float noiseSeed;
    private bool snapOffsets;

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

        // Level to start with, unless the plane is spawned pointing at the sky, where
        // there is no level to be...
        Vector3 forward = target.forward;
        Vector3 up = Mathf.Abs(Vector3.Dot(forward, Vector3.up)) > 0.99f ? target.up : Vector3.up;

        // ...and leaning toward the wings by however much the camera is going to lean
        // toward them anyway, so a plane spawned or respawned banked does not start
        // level and then roll over on the first frame.
        up = Vector3.Slerp(up, target.up, rollMatch);
        if (up.sqrMagnitude < 1e-6f)
            up = target.up;

        rigRotation = Quaternion.LookRotation(forward, up);
        lastTargetRotation = target.rotation;

        rigOffset = rigRotation * new Vector3(0f, followHeight, -followDistance);
        aimOffset = rigRotation * new Vector3(0f, aimHeight, lookAhead);
        rigPosition = target.position + rigOffset;

        rigOffsetVelocity = Vector3.zero;
        aimOffsetVelocity = Vector3.zero;
        swing = Vector3.zero;

        // What a level, unaccelerated plane already feels, so the swing starts at rest
        // rather than lurching once on the first frame.
        smoothedForce = -Physics.gravity;
        lastTargetVelocity = TargetVelocity();

        collisionDistance = rigOffset.magnitude;
        trauma = 0f;
        snapOffsets = false;

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

        UpdateLookBehind();

        // Clamped, or flying past the reference speed keeps widening the lens until the
        // whole world is a fisheye.
        float speedRatio = Mathf.Clamp01(TargetVelocity().magnitude / Mathf.Max(referenceSpeed, 0.01f));

        UpdateRotation(dt);
        UpdateOffsets(speedRatio, dt);
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

        // Specific force: what an accelerometer bolted to the plane reads, which is the
        // acceleration minus gravity. It is what a pilot feels, and unlike raw
        // acceleration it is zero in free fall instead of a steady 1 g downward -- so
        // pushing over the top of a climb floats the camera rather than hurling it.
        Vector3 force = acceleration - Physics.gravity;

        // Checked before smoothing, or the spike is averaged away before anyone sees it.
        // A hard turn is worth a few g, so a threshold well clear of that only trips on
        // something that really was a wall.
        float magnitude = force.magnitude;
        if (magnitude > impactThreshold)
            AddTrauma(Mathf.InverseLerp(impactThreshold, impactThreshold * 3f, magnitude) * impactShake);

        smoothedForce = Vector3.Lerp(smoothedForce, force, Blend(accelerationSmoothing, dt));
    }

    // --- The rig -----------------------------------------------------------

    private void UpdateLookBehind()
    {
        bool wants = selectLooksBehind && GbaInput.Held(GbaInput.Button.Select);
        if (wants == LookingBehind)
            return;

        // Sixteen metres is too far for a spring to cross gracefully, so the rig is
        // teleported through the plane rather than swung around it.
        LookingBehind = wants;
        snapOffsets = true;
    }

    /// <summary>
    /// Turn the rig onto the nose, then deal with roll separately.
    ///
    /// Built out of relative rotations rather than a LookRotation, because a
    /// LookRotation needs an up axis and every choice of up axis has two points where
    /// it is useless -- straight up and straight down. Pointing a plane at either is a
    /// perfectly ordinary thing to do, and a camera that spins when you do it is the
    /// single most common reason a chase camera feels wrong.
    /// </summary>
    private void UpdateRotation(float dt)
    {
        // How much the plane rolled since the last frame, taken off the rotation itself
        // rather than measured against the horizon. That works upside down, sideways
        // and mid-loop, where a bank angle does not.
        Quaternion turn = target.rotation * Quaternion.Inverse(lastTargetRotation);
        lastTargetRotation = target.rotation;

        turn.ToAngleAxis(out float turned, out Vector3 axis);
        if (turned > 180f) turned -= 360f;
        float rolled = float.IsNaN(axis.x) ? 0f : turned * Vector3.Dot(axis, target.forward);

        // Where the rig wants to point: down the nose, nudged toward where the plane is
        // actually travelling so a slipping plane still reads against its flight path.
        Vector3 goalForward = target.forward;
        Vector3 velocity = TargetVelocity();
        if (flightPathBlend > 0f && velocity.sqrMagnitude > 1f)
            goalForward = Vector3.Slerp(goalForward, velocity.normalized, flightPathBlend).normalized;

        // The shortest rotation from where the rig looks to where it wants to look:
        // a pure swing with no twist in it, and no pole anywhere.
        Vector3 rigForward = rigRotation * Vector3.forward;
        Quaternion toGoal = Quaternion.FromToRotation(rigForward, goalForward);
        rigRotation = Quaternion.Slerp(Quaternion.identity, toGoal, Blend(turnFollow, dt)) * rigRotation;

        // Take a share of the plane's roll...
        rigRotation *= Quaternion.AngleAxis(rolled * rollFollow, Vector3.forward);

        // ...then roll the rest of the way onto the wings. Measured against the plane's
        // own up rather than the world's, which is what makes this hold through inverted
        // flight and knife edge: the plane's up is never undefined, whereas the horizon
        // is exactly the thing that stops existing when you point the nose at the sky.
        if (rollMatch > 0f)
        {
            Vector3 wingAxis = rigRotation * Vector3.forward;
            Vector3 wings = Vector3.ProjectOnPlane(target.up, wingAxis);

            // Degenerate only if the rig has ended up looking straight along the plane's
            // up axis, which is a quarter turn off the nose and not somewhere it goes.
            if (wings.sqrMagnitude > 1e-6f)
            {
                float toWings = Vector3.SignedAngle(rigRotation * Vector3.up, wings.normalized, wingAxis);
                rigRotation *= Quaternion.AngleAxis(toWings * rollMatch * Blend(rollMatchRate, dt), Vector3.forward);
            }
        }

        // Whatever roll is left over is handed back to the horizon over the next second
        // or so. Copying the roll as it happens makes a barrel roll feel connected;
        // giving it back makes a held bank readable. Scaled out as the camera commits to
        // the wings instead, or the two would spend the whole flight arguing. Both are
        // skipped with the nose near vertical, where the horizon is not a direction any
        // more and the measurement below is pure noise.
        float righting = 1f - rollMatch;
        Vector3 forward = rigRotation * Vector3.forward;
        Vector3 levelUp = Vector3.ProjectOnPlane(Vector3.up, forward);
        if (righting > 0f && levelUp.sqrMagnitude > 1e-4f)
        {
            float horizon = Mathf.Clamp01(levelUp.magnitude * 2f);
            float bank = Vector3.SignedAngle(levelUp.normalized, rigRotation * Vector3.up, forward);

            // Eased rather than clamped outright, so coming back from a vertical climb
            // with the horizon wound past the limit unwinds instead of snapping.
            float goal = Mathf.Lerp(bank, Mathf.Clamp(bank, -maxRoll, maxRoll), Blend(rollLevel * 3f, dt));
            goal = Mathf.Lerp(goal, 0f, horizon * Blend(rollLevel, dt));

            rigRotation *= Quaternion.AngleAxis((goal - bank) * righting, Vector3.forward);
        }
    }

    private void UpdateOffsets(float speedRatio, float dt)
    {
        float behind = LookingBehind ? -1f : 1f;

        // Faster means further back, which widens the frame exactly when you need more
        // warning about what is coming.
        float distance = followDistance + speedRatio * distanceGain;

        // The g-load read in the camera's own axes, less the 1 g that straight and level
        // flight already carries, then pushed back the other way: pull g in a left turn
        // and the camera slides out to the right.
        Vector3 localForce = Quaternion.Inverse(rigRotation) * smoothedForce;
        localForce.y -= Physics.gravity.magnitude;
        Vector3 swingGoal = Vector3.ClampMagnitude(-localForce * swingGain, maxSwing);
        swing = Vector3.Lerp(swing, swingGoal, Blend(swingFollow, dt));

        Vector3 goalOffset = rigRotation * (new Vector3(0f, followHeight, -distance * behind) + swing);
        Vector3 goalAim = rigRotation * new Vector3(0f, aimHeight, lookAhead * behind);

        if (snapOffsets)
        {
            snapOffsets = false;
            rigOffset = goalOffset;
            aimOffset = goalAim;
            rigOffsetVelocity = Vector3.zero;
            aimOffsetVelocity = Vector3.zero;
            collisionDistance = goalOffset.magnitude;
        }
        else
        {
            Spring(ref rigOffset, ref rigOffsetVelocity, goalOffset, followFrequency, followDamping, dt);
            Spring(ref aimOffset, ref aimOffsetVelocity, goalAim, aimFrequency, aimDamping, dt);
        }

        rigPosition = AvoidGeometry(target.position, target.position + rigOffset, dt);
    }

    /// <summary>
    /// Pull the camera in if there is a wall between it and the plane. The sphere is
    /// cast from the plane outward, and Unity does not report colliders the sphere is
    /// already inside at the start of the cast, which is exactly what keeps the plane's
    /// own hull from counting as a wall.
    /// </summary>
    private Vector3 AvoidGeometry(Vector3 from, Vector3 desired, float dt)
    {
        if (!avoidGeometry)
            return desired;

        Vector3 delta = desired - from;
        float wanted = delta.magnitude;
        if (wanted < 1e-4f)
            return desired;

        Vector3 direction = delta / wanted;
        float allowed = wanted;

        if (Physics.SphereCast(from, collisionRadius, direction, out RaycastHit hit, wanted,
                collisionMask, QueryTriggerInteraction.Ignore))
            allowed = Mathf.Max(hit.distance, minDistance);

        // Straight in, eased out. Being late to duck puts a wall through the frame;
        // being late to come back out is invisible.
        collisionDistance = allowed < collisionDistance
            ? allowed
            : Mathf.Lerp(collisionDistance, allowed, Blend(pullOutRate, dt));

        return from + direction * Mathf.Min(collisionDistance, wanted);
    }

    private Quaternion AimRotation(float dt)
    {
        Quaternion view = LookingBehind
            ? rigRotation * Quaternion.AngleAxis(180f, Vector3.up)
            : rigRotation;

        Vector3 toAim = (target.position + aimOffset) - rigPosition;
        if (toAim.sqrMagnitude < 1e-4f)
            return view;

        // Blended rather than used outright: tracking the aim point exactly pins the
        // plane to the middle of the screen and throws away everything the springs did.
        Quaternion look = Quaternion.LookRotation(toAim, view * Vector3.up);
        return Quaternion.Slerp(view, look, aimTracking);
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
        // Scaled by how deep the stall is, so it comes in as a shiver and builds.
        if (plane != null)
            trauma = Mathf.Max(trauma, stallShake * plane.StallFactor);

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
