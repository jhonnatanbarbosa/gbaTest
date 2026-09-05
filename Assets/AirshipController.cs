using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Arcade flight model. W/S pitch, A/D roll, Q/E throttle.
///
/// Turning is not scripted: bank the wings with A/D and the lift vector tilts with
/// them, which curves the velocity. The tail then swings the nose around to follow.
/// That also means a hard bank costs you altitude, exactly like the real thing, so
/// pull back on W while you turn.
///
/// Thrust, drag and lift all scale with mass, so changing the Rigidbody mass does
/// not force you to retune anything.
///
/// Reads Keyboard.current directly because this project is set to
/// "Input System Package (New)", where UnityEngine.Input would throw.
/// </summary>
[RequireComponent(typeof(Rigidbody))]
public class AirshipController : MonoBehaviour
{
    [Header("Throttle")]
    [Tooltip("Thrust at full throttle, as an acceleration. Top speed is where this balances drag.")]
    [SerializeField] private float maxThrust = 30f;
    [Tooltip("How fast Q and E move the throttle across its whole range, per second.")]
    [SerializeField] private float throttleChangeRate = 0.5f;
    [SerializeField, Range(0f, 1f)] private float startThrottle = 0.6f;

    [Header("Wing")]
    [Tooltip("Airspeed at which the wings exactly carry the plane. Slower and it sinks, faster and it climbs.")]
    [SerializeField] private float cruiseSpeed = 20f;
    [Tooltip("Below this airspeed the wing stalls: lift fades and the controls go mushy.")]
    [SerializeField] private float stallSpeed = 8f;
    [Tooltip("Ceiling on lift, in multiples of the plane's own weight. Roughly the g-load it can pull.")]
    [SerializeField] private float maxLiftG = 3f;
    [Tooltip("Extra lift a full pull on the stick asks for, in weights. This is what makes it climb.")]
    [SerializeField] private float elevatorPower = 2f;

    [Header("Drag")]
    [SerializeField] private float forwardDrag = 0.04f;
    [Tooltip("Resistance to sliding sideways. Keep this high or the plane skids through turns.")]
    [SerializeField] private float sideDrag = 1f;
    [SerializeField] private float verticalDrag = 0.15f;

    [Header("Controls (degrees per second)")]
    [SerializeField] private float pitchSpeed = 50f;
    [SerializeField] private float rollSpeed = 100f;
    [Tooltip("Off: W drops the nose, like pushing a flight stick forward. On: W lifts it.")]
    [SerializeField] private bool invertPitch = false;
    [Tooltip("Off: nobody reads the keyboard, and an AI pilot flies this plane through PitchInput, RollInput and ThrottleInput.")]
    [SerializeField] private bool playerControlled = true;

    [Header("Stability")]
    [Tooltip("How hard the wings roll back to level once you let go of A/D. Zero for a neutral, aerobatic plane.")]
    [SerializeField] private float rollAutoLevel = 50f;
    [Tooltip("Tail authority: how quickly the nose swings to follow the direction of travel.")]
    [SerializeField] private float tailStrength = 3f;

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

    /// <summary>Throttle setting, 0 to 1.</summary>
    public float Throttle => throttle;
    /// <summary>Speed along the nose, which is what the wings actually care about.</summary>
    public float Airspeed => airspeed;
    /// <summary>True while the wing is below its stall speed.</summary>
    public bool IsStalling => airspeed < stallSpeed;

    private Rigidbody rb;
    private float throttle;
    private float airspeed;

    private float pitchInput;
    private float rollInput;

    private void Awake()
    {
        throttle = startThrottle;

        rb = GetComponent<Rigidbody>();
        rb.useGravity = true;
        rb.linearDamping = 0f;      // drag is modelled per axis below
        rb.angularDamping = 5f;
        rb.interpolation = RigidbodyInterpolation.Interpolate;
        rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
    }

    private void Update()
    {
        if (!playerControlled)
            return;

        Keyboard keyboard = Keyboard.current;
        if (keyboard == null)
            return;

        pitchInput = 0f;
        if (keyboard.wKey.isPressed) pitchInput += 1f;
        if (keyboard.sKey.isPressed) pitchInput -= 1f;
        if (invertPitch) pitchInput = -pitchInput;

        rollInput = 0f;
        if (keyboard.aKey.isPressed) rollInput += 1f;
        if (keyboard.dKey.isPressed) rollInput -= 1f;

        float throttleInput = 0f;
        if (keyboard.eKey.isPressed) throttleInput += 1f;
        if (keyboard.qKey.isPressed) throttleInput -= 1f;

        throttle = Mathf.Clamp01(throttle + throttleInput * throttleChangeRate * Time.deltaTime);
    }

    private void FixedUpdate()
    {
        Vector3 velocity = rb.linearVelocity;
        airspeed = Vector3.Dot(velocity, transform.forward);

        // Everything aerodynamic fades as the wing stalls, so a slow plane both
        // sinks and stops answering the stick.
        float wingPower = Mathf.Clamp01(airspeed / Mathf.Max(stallSpeed, 0.01f));

        ApplyForces(velocity, wingPower);
        ApplyRotation(velocity, wingPower, Time.fixedDeltaTime);
    }

    private void ApplyForces(Vector3 velocity, float wingPower)
    {
        float mass = rb.mass;
        float weight = mass * Physics.gravity.magnitude;

        // How much lift the wing *can* make here. Grows with the square of airspeed,
        // calibrated so cruiseSpeed is exactly enough to carry the plane.
        float speedRatio = airspeed / Mathf.Max(cruiseSpeed, 0.01f);
        float available = Mathf.Clamp(weight * speedRatio * speedRatio, 0f, weight * maxLiftG) * wingPower;

        // How much it asks for. Left alone the wing trims itself to carry exactly the
        // plane's weight, so extra speed buys speed rather than an unwanted climb.
        // Bank tilts that lift sideways, so holding a level turn needs proportionally
        // more of it, which is where the g-load in a hard turn comes from.
        Vector3 levelUp = Vector3.ProjectOnPlane(Vector3.up, transform.forward);
        float cosBank = levelUp.sqrMagnitude > 0.001f
            ? Vector3.Dot(transform.up, levelUp.normalized)
            : 1f;
        float loadFactor = Mathf.Clamp(1f / Mathf.Max(cosBank, 0.25f), 0f, maxLiftG);

        // Pulling back asks for more than trim, pushing asks for less.
        float commanded = weight * (loadFactor - pitchInput * elevatorPower);

        // Whichever is smaller: a wing cannot make lift it does not have the speed for.
        float lift = Mathf.Clamp(Mathf.Min(commanded, available), 0f, weight * maxLiftG);
        rb.AddForce(transform.up * lift);

        rb.AddForce(transform.forward * (throttle * maxThrust * mass));

        // Drag is measured in the plane's own axes: a wing barely resists moving
        // forward but fights hard against sliding sideways.
        Vector3 localVelocity = transform.InverseTransformDirection(velocity);
        Vector3 localDrag = new Vector3(
            -localVelocity.x * Mathf.Abs(localVelocity.x) * sideDrag,
            -localVelocity.y * Mathf.Abs(localVelocity.y) * verticalDrag,
            -localVelocity.z * Mathf.Abs(localVelocity.z) * forwardDrag);
        rb.AddForce(transform.TransformDirection(localDrag) * mass);
    }

    private void ApplyRotation(Vector3 velocity, float wingPower, float dt)
    {
        // A positive local X rotation drops the nose and a positive Z rolls left.
        float pitchRate = pitchInput * pitchSpeed;
        float rollRate = rollInput * rollSpeed;
        float yawRate = 0f;

        // Dihedral: with the stick centred the wings drift back to level on their own.
        // bankRight is positive when the right wing is low, and fades to zero when the
        // nose points straight up or down, where rolling and yawing are the same thing.
        // A deadzone rather than an exact zero, so an AI pilot easing off the stick
        // still gets the wings levelled for it.
        if (Mathf.Abs(rollInput) < 0.05f)
        {
            float bankRight = -Vector3.Dot(transform.right, Vector3.up);
            rollRate += bankRight * rollAutoLevel;
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
