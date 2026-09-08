using UnityEngine;

/// <summary>
/// Arcade flight model, flown the way GTA V flies: real forces underneath, a
/// generous invisible co-pilot on top.
///
/// Nothing about the turn is scripted. Banking tilts the lift vector, the sideways
/// part of it curves the velocity, and the tail swings the nose round to follow. A
/// hard bank therefore costs you altitude unless you pull back, exactly like the
/// real thing. What GTA V adds -- and what <see cref="flightAssist"/> adds here --
/// is everything a pilot would be doing with their feet and their trim: the rudder
/// is fed into the turn for you, the wings roll back level and the nose finds the
/// horizon the moment you take your thumbs off the D-pad. Drop the assist to zero
/// and the same aeroplane becomes a twitchy aerobatic one.
///
/// Thrust, lift and drag are all per unit mass, so changing the Rigidbody mass does
/// not force you to retune anything.
///
/// The eight buttons this console has:
///   D-pad up/down     pitch
///   D-pad left/right  roll, which is how you turn
///   A                 throttle up
///   B                 throttle down, and the airbrake once it bottoms out
/// </summary>
[RequireComponent(typeof(Rigidbody))]
public class AirshipController : MonoBehaviour
{
    [Header("Throttle")]
    [Tooltip("Thrust at full throttle, as an acceleration. Top speed is where this balances drag.")]
    [SerializeField] private float maxThrust = 30f;
    [Tooltip("How fast A and B move the throttle across its whole range, per second.")]
    [SerializeField] private float throttleChangeRate = 0.6f;
    [SerializeField, Range(0f, 1f)] private float startThrottle = 0.7f;
    [Tooltip("How many times forward drag multiplies when B is held with the throttle already shut. This is the only brake a plane has.")]
    [SerializeField] private float airbrakeDrag = 6f;

    [Header("Wing")]
    [Tooltip("Airspeed at which the wings exactly carry the plane. Slower and it sinks, faster and it climbs.")]
    [SerializeField] private float cruiseSpeed = 20f;
    [Tooltip("Below this airspeed the wing stalls: lift fades and the controls go mushy.")]
    [SerializeField] private float stallSpeed = 8f;
    [Tooltip("Ceiling on lift, in multiples of the plane's own weight. Roughly the g-load it can pull, and so how tight it can turn.")]
    [SerializeField] private float maxLiftG = 3f;
    [Tooltip("Extra lift a full pull on the stick asks for, in weights. This is what makes it climb.")]
    [SerializeField] private float elevatorPower = 2f;
    [Tooltip("How much downforce the wing can make, as a fraction of the lift it can make. A real wing at a negative angle of attack pushes toward the belly, which is what holds a plane up inverted and what lets a knife-edge turn go both ways. 0 is the old behaviour, where the wing could only ever push toward the top of the plane.")]
    [SerializeField, Range(0f, 1f)] private float inverseLift = 0.65f;

    [Header("Drag")]
    [SerializeField] private float forwardDrag = 0.04f;
    [Tooltip("Resistance to sliding sideways. Keep this high or the plane skids through turns.")]
    [SerializeField] private float sideDrag = 1f;
    [SerializeField] private float verticalDrag = 0.15f;

    [Header("Controls (degrees per second)")]
    [SerializeField] private float pitchSpeed = 55f;
    [SerializeField] private float rollSpeed = 120f;
    [Tooltip("On, and like GTA V and every flight stick ever made: up dips the nose and down pulls it up. Off: up climbs, like an arcade shooter.")]
    [SerializeField] private bool stickPitch = true;
    [Tooltip("How fast a held direction ramps to full deflection, per second. A D-pad has no in-between positions, so this is what stops the plane snapping to full stick the instant you touch it.")]
    [SerializeField] private float stickAttack = 6f;
    [Tooltip("How fast the stick springs back once you let go, per second.")]
    [SerializeField] private float stickRelease = 10f;
    [Tooltip("Off: nobody reads the buttons, and an AI pilot flies this plane through PitchInput, RollInput and ThrottleInput.")]
    [SerializeField] private bool playerControlled = true;

    [Header("Assists")]
    [Tooltip("The invisible co-pilot, as one knob. 1 is GTA V -- it is hard to hurt yourself. 0 hands you the raw aeroplane.")]
    [SerializeField, Range(0f, 1f)] private float flightAssist = 1f;
    [Tooltip("How hard the wings roll back to level once you let go of left/right. This is measured at 45 degrees of bank and scales down from there.")]
    [SerializeField] private float rollAutoLevel = 45f;
    [Tooltip("How hard the nose comes back to the horizon once you let go of up/down. Only acts while roughly upright, so it cannot fly you into the ground while inverted.")]
    [SerializeField] private float pitchAutoLevel = 30f;
    [Tooltip("Rudder the co-pilot feeds into a bank, in degrees per second at knife edge. This is what makes the nose come round with the turn instead of trailing a second behind it.")]
    [SerializeField] private float yawAssist = 25f;

    [Header("Stability")]
    [Tooltip("Tail authority: how quickly the nose swings to follow the direction of travel.")]
    [SerializeField] private float tailStrength = 3f;

    [Header("Altimeter")]
    [Tooltip("What counts as ground. Everything by default, so rooftops read as ground too and the gauge tells you what you are about to hit rather than where sea level is. Restrict it to a terrain layer if you would rather the number ignored buildings.")]
    [SerializeField] private LayerMask groundMask = ~0;
    [Tooltip("How far down to look for ground. Past this the last height found is reused, so flying off the edge of the level holds the reading instead of dropping it to nothing.")]
    [SerializeField] private float groundProbe = 500f;

    [Header("Spawn")]
    [Tooltip("On: the plane starts at cruise speed rather than dropping out of the sky and stall-shaking the camera for the first second of the level.")]
    [SerializeField] private bool startFlying = true;

    /// <summary>Stick input, -1 to 1, where positive drops the nose.</summary>
    public float PitchInput
    {
        get => pitchInput;
        set => pitchInput = Mathf.Clamp(value, -1f, 1f);
    }

    /// <summary>Stick input, -1 to 1, where positive rolls left.</summary>
    public float RollInput
    {
        get => rollInput;
        set => rollInput = Mathf.Clamp(value, -1f, 1f);
    }

    /// <summary>Throttle setting, 0 to 1. Useful for a HUD or engine audio.</summary>
    public float ThrottleInput
    {
        get => throttle;
        set => throttle = Mathf.Clamp01(value);
    }

    /// <summary>Airbrake, for an AI pilot. The player gets it by holding B at zero throttle.</summary>
    public bool Brake { get; set; }

    /// <summary>Throttle setting, 0 to 1.</summary>
    public float Throttle => throttle;
    /// <summary>Speed along the nose, which is what the wings actually care about.</summary>
    public float Airspeed => airspeed;
    /// <summary>Speed through the air in any direction, which is what the camera cares about.</summary>
    public float Speed => rb != null ? rb.linearVelocity.magnitude : 0f;
    /// <summary>
    /// Height above whatever is directly below, not above the world origin. Those are
    /// only the same thing in a level that was built on y = 0, and this one is not:
    /// sitting on the ground used to read as the height of the ground itself.
    /// </summary>
    public float Altitude => altitude;
    /// <summary>Airspeed at which the wing gives up, so a HUD can mark it.</summary>
    public float StallSpeed => stallSpeed;
    /// <summary>Wings-level is 0, knife edge is +-90, inverted is +-180. Positive is banked left.</summary>
    public float BankAngle => bank;
    /// <summary>0 flying, 1 with no air over the wings at all.</summary>
    public float StallFactor => 1f - Mathf.Clamp01(airspeed / Mathf.Max(stallSpeed, 0.01f));
    /// <summary>True while the wing has lost enough air to matter.</summary>
    public bool IsStalling => StallFactor > 0.15f;

    private const float Deadzone = 0.05f;

    private Rigidbody rb;
    private float throttle;
    private float airspeed;

    private float pitchInput;
    private float rollInput;

    // Attitude, measured once a physics step and shared by the forces and the rotation.
    private float bank;         // degrees, 0 wings level
    private float cosBank;      // 1 upright, 0 knife edge, -1 inverted
    private float noseLevel;    // 1 nose on the horizon, 0 nose straight up or down

    private float altitude;
    private float groundHeight; // last ground found under the plane, so a missed probe holds rather than jumps

    // Reused, because this is a probe a physics step and a fresh array each time would
    // be a fresh trip to the garbage collector each time.
    private readonly RaycastHit[] groundHits = new RaycastHit[8];

    private void Awake()
    {
        throttle = startThrottle;

        rb = GetComponent<Rigidbody>();
        rb.useGravity = true;
        rb.linearDamping = 0f;      // drag is modelled per axis below
        rb.angularDamping = 5f;
        rb.interpolation = RigidbodyInterpolation.Interpolate;
        rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;

        // In Awake rather than Start, and it matters. Every Awake runs before any
        // Start, and the chase camera takes its first velocity reading in its Start:
        // launch the plane any later than this and the camera sees nought to cruise
        // speed inside one physics step, reads a hundred g, and hits you with the
        // collision shake on the first frame of the game.
        if (startFlying)
            rb.linearVelocity = transform.forward * cruiseSpeed;

        MeasureAttitude();
        MeasureAltitude();
        airspeed = Vector3.Dot(rb.linearVelocity, transform.forward);
    }

    private void Update()
    {
        if (!playerControlled)
            return;

        float dt = Time.deltaTime;

        // A D-pad has two positions and a stick has a hundred. Ramping the digital
        // input into an analogue one is the whole difference between a plane that
        // snaps between hard left and hard right and a plane you can fly.
        float pitchTarget = GbaInput.Axis(GbaInput.Button.Down, GbaInput.Button.Up);
        if (!stickPitch) pitchTarget = -pitchTarget;
        float rollTarget = GbaInput.Axis(GbaInput.Button.Right, GbaInput.Button.Left);

        pitchInput = MoveStick(pitchInput, pitchTarget, dt);
        rollInput = MoveStick(rollInput, rollTarget, dt);

        float throttleTarget = GbaInput.Axis(GbaInput.Button.B, GbaInput.Button.A);
        throttle = Mathf.Clamp01(throttle + throttleTarget * throttleChangeRate * dt);

        // The airbrake is what B does once there is no throttle left to take away, so
        // one button covers both "slow down" and "slow down a lot".
        Brake = GbaInput.Held(GbaInput.Button.B) && throttle <= 0.001f;
    }

    /// <summary>Springs toward a held direction, and back to centre faster than it left.</summary>
    private float MoveStick(float current, float target, float dt)
    {
        bool returning = Mathf.Abs(target) < Mathf.Abs(current) || target * current < 0f;
        float rate = returning ? stickRelease : stickAttack;
        return Mathf.MoveTowards(current, target, rate * dt);
    }

    private void FixedUpdate()
    {
        Vector3 velocity = rb.linearVelocity;
        airspeed = Vector3.Dot(velocity, transform.forward);

        // Everything aerodynamic fades as the wing stalls, so a slow plane both
        // sinks and stops answering the stick.
        float wingPower = Mathf.Clamp01(airspeed / Mathf.Max(stallSpeed, 0.01f));

        MeasureAttitude();
        MeasureAltitude();
        ApplyForces(velocity, wingPower);
        ApplyRotation(velocity, wingPower, Time.fixedDeltaTime);
    }

    /// <summary>
    /// Height above the ground, found by looking for it rather than by assuming it is
    /// at y = 0. Nothing says a level is built around the world origin -- this one is
    /// laid out twenty metres up -- and an altimeter that measures from the origin
    /// reads the height of the scenery even with the plane sat on top of it.
    /// </summary>
    private void MeasureAltitude()
    {
        Vector3 origin = transform.position;

        // RaycastNonAlloc rather than Raycast, because the first thing under the plane
        // is usually the plane, and a single-hit raycast has no way to skip it and keep
        // looking. Hits come back unsorted, and since the ray points down, the nearest
        // ground is simply the highest one.
        int count = Physics.RaycastNonAlloc(new Ray(origin, Vector3.down), groundHits,
            groundProbe, groundMask, QueryTriggerInteraction.Ignore);

        float found = float.NegativeInfinity;
        for (int i = 0; i < count; i++)
        {
            if (groundHits[i].rigidbody == rb)
                continue;

            if (groundHits[i].point.y > found)
                found = groundHits[i].point.y;
        }

        if (found > float.NegativeInfinity)
            groundHeight = found;

        altitude = origin.y - groundHeight;
    }

    /// <summary>
    /// Where the plane is pointing relative to the horizon. Bank and "level" both stop
    /// meaning anything with the nose straight up, where rolling and yawing are the
    /// same motion, so <see cref="noseLevel"/> fades every assist out before they get
    /// there rather than letting them fight over a degenerate answer.
    /// </summary>
    private void MeasureAttitude()
    {
        Vector3 forward = transform.forward;
        Vector3 up = transform.up;

        Vector3 projected = Vector3.ProjectOnPlane(Vector3.up, forward);

        // The length of that projection is the sine of how far the nose is off vertical:
        // 1 pointing at the horizon, 0 pointing at the sky. Doubled and clamped, so the
        // assists stay at full strength until the nose is 30 degrees up and are gone by 90.
        noseLevel = Mathf.Clamp01(projected.magnitude * 2f);

        if (projected.sqrMagnitude > 1e-6f)
        {
            Vector3 levelUp = projected.normalized;
            bank = Vector3.SignedAngle(levelUp, up, forward);
            cosBank = Vector3.Dot(up, levelUp);
        }
        else
        {
            bank = 0f;
            cosBank = 1f;
        }
    }

    private void ApplyForces(Vector3 velocity, float wingPower)
    {
        float mass = rb.mass;
        float weight = mass * Physics.gravity.magnitude;

        // How much lift the wing *can* make here. Grows with the square of airspeed,
        // calibrated so cruiseSpeed is exactly enough to carry the plane.
        float speedRatio = airspeed / Mathf.Max(cruiseSpeed, 0.01f);
        float available = Mathf.Min(weight * speedRatio * speedRatio, weight * maxLiftG) * wingPower;

        // How much it asks for. Left alone the wing trims itself to carry exactly the
        // plane's weight, so extra speed buys speed rather than an unwanted climb.
        // Bank tilts that lift sideways, and only cos(bank) of it is still fighting
        // gravity, so holding a level turn needs 1/cos(bank) times as much: that is
        // where the g-load in a hard turn comes from. Past ninety degrees of bank there
        // is nothing to trim for -- the wing is pointing at the ground -- and with the
        // nose at the sky the wing is not carrying the plane at all.
        float trim = cosBank > 0f ? Mathf.Min(1f / Mathf.Max(cosBank, 0.2f), maxLiftG) : 0f;
        trim *= noseLevel;

        // Pulling back asks for more than trim, pushing asks for less.
        float commanded = weight * (trim - pitchInput * elevatorPower);

        // Whichever is smaller: a wing cannot make lift it does not have the speed for.
        //
        // The floor is negative rather than zero, and on knife edge that is the whole
        // difference between a control that works and one that does nothing. Sideways
        // is where lift points there, so lift *is* the turn: pulling asks for more of
        // it and curves one way, pushing asks for less than nothing and should curve
        // the other. Clamped at zero, pushing could only ever cancel the turn, never
        // reverse it -- the plane just stopped turning and fell out of the knife edge,
        // while the same input pulled the other way turned fine. Same fix lets the wing
        // hold the plane up while inverted instead of quietly giving up.
        float lift = Mathf.Clamp(commanded, -available * inverseLift, available);
        rb.AddForce(transform.up * lift);

        rb.AddForce(transform.forward * (throttle * maxThrust * mass));

        // Drag is measured in the plane's own axes: a wing barely resists moving
        // forward but fights hard against sliding sideways. The airbrake is nothing
        // more than turning that first number up.
        float along = Brake ? forwardDrag * airbrakeDrag : forwardDrag;
        Vector3 localVelocity = transform.InverseTransformDirection(velocity);
        Vector3 localDrag = new Vector3(
            -localVelocity.x * Mathf.Abs(localVelocity.x) * sideDrag,
            -localVelocity.y * Mathf.Abs(localVelocity.y) * verticalDrag,
            -localVelocity.z * Mathf.Abs(localVelocity.z) * along);
        rb.AddForce(transform.TransformDirection(localDrag) * mass);
    }

    private void ApplyRotation(Vector3 velocity, float wingPower, float dt)
    {
        // A positive local X rotation drops the nose, a positive Y yaws right and a
        // positive Z rolls left.
        float pitchRate = pitchInput * pitchSpeed;
        float rollRate = rollInput * rollSpeed;
        float yawRate = 0f;

        // Turn coordination. The bank is already curving the flight path on its own;
        // this is the rudder a pilot would be feeding in at the same time, so the nose
        // comes round with the turn rather than trailing behind it while the tail
        // catches up. dot(right, up) is the sine of the bank, read straight off the
        // wing, and it is correctly zero both level and inverted.
        yawRate += -Vector3.Dot(transform.right, Vector3.up) * yawAssist * flightAssist;

        // Dihedral, with a pilot's hands on it. Full correction from 45 degrees of bank
        // outward, which unlike a dot product keeps working past knife edge and rolls
        // the plane back upright from inverted instead of sitting there.
        // A deadzone rather than an exact zero, so an AI pilot easing off the stick
        // still gets the wings levelled for it.
        if (Mathf.Abs(rollInput) < Deadzone)
            rollRate += Mathf.Clamp(-bank / 45f, -1f, 1f) * rollAutoLevel * flightAssist * noseLevel;

        // And the same for the nose, which is what stops a beginner spiralling into the
        // ground. Faded out with cos(bank), because "level" while inverted means
        // pushing the nose at the scenery.
        if (Mathf.Abs(pitchInput) < Deadzone)
        {
            float climb = Mathf.Asin(Mathf.Clamp(Vector3.Dot(transform.forward, Vector3.up), -1f, 1f)) * Mathf.Rad2Deg;
            pitchRate += Mathf.Clamp(climb / 45f, -1f, 1f) * pitchAutoLevel * flightAssist * Mathf.Clamp01(cosBank);
        }

        // The tail. Measure where the airflow is coming from and swing the nose onto
        // it, which is what stops the plane flying sideways and drops the nose in a stall.
        if (velocity.sqrMagnitude > 1f)
        {
            Vector3 localFlow = transform.InverseTransformDirection(velocity.normalized);
            float angleOfAttack = Mathf.Atan2(localFlow.y, localFlow.z) * Mathf.Rad2Deg;
            float sideslip = Mathf.Atan2(localFlow.x, localFlow.z) * Mathf.Rad2Deg;

            pitchRate += -angleOfAttack * tailStrength;
            yawRate += sideslip * tailStrength;
        }

        float authority = wingPower * dt;
        Quaternion step = Quaternion.Euler(
            pitchRate * authority,
            yawRate * authority,
            rollRate * authority);

        rb.MoveRotation(rb.rotation * step);
        rb.angularVelocity = Vector3.zero;   // rotation is flown, not tumbled
    }
}
