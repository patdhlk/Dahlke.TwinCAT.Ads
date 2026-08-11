namespace Dahlke.EtherCAT.Cia402;

/// <summary>
/// The eight states of the CiA-402 drive state machine, plus one for a statusword the standard does
/// not name.
/// </summary>
public enum Cia402State
{
    /// <summary>
    /// The drive is initialising. Self-test and boot; the state machine is not yet answering
    /// commands, and a drive normally passes through here without a caller ever observing it.
    /// </summary>
    NotReadyToSwitchOn,

    /// <summary>
    /// Initialisation is finished and the drive is waiting to be asked to switch on. This is the
    /// state a healthy, idle, un-commanded drive sits in — and the state a fault reset lands in.
    /// </summary>
    SwitchOnDisabled,

    /// <summary>The Shutdown command has been accepted; the drive will accept Switch on next.</summary>
    ReadyToSwitchOn,

    /// <summary>
    /// Power stage is on but the drive is not following a setpoint. Enable operation moves it on to
    /// <see cref="OperationEnabled"/>.
    /// </summary>
    SwitchedOn,

    /// <summary>
    /// The drive is enabled and following its setpoint. The one state in which a motion command
    /// does anything.
    /// </summary>
    OperationEnabled,

    /// <summary>
    /// A quick stop is being carried out or is being held. What the drive does here — ramp, coast,
    /// hold — is its own <c>0x605A</c> quick stop option, not something this state says.
    /// </summary>
    QuickStopActive,

    /// <summary>
    /// A fault has been detected and the drive is executing its fault reaction (typically a stop).
    /// It moves to <see cref="Fault"/> on its own when the reaction completes; a fault reset here is
    /// premature.
    /// </summary>
    FaultReactionActive,

    /// <summary>
    /// A fault is latched and the drive is stopped. It stays here until a fault reset (a rising edge
    /// of controlword bit 7) clears it, and only then if the cause is gone.
    /// </summary>
    Fault,

    /// <summary>
    /// The statusword matched no row of the CiA-402 state table.
    ///
    /// <para>
    /// This is a member because the state table does NOT cover all 65536 words, and real drives
    /// produce words outside it. <c>0x1591</c>, read off the drive whose commissioning prompted this
    /// package, has bit 0 set while the quick stop bit is clear — a combination the standard's table
    /// simply does not list. Reporting the nearest row would be a fabrication, and reporting a state
    /// the drive is not in is the failure this whole package exists to prevent.
    /// </para>
    /// <para>
    /// The flags on <see cref="Cia402Status"/> are still decoded when the state is unknown: they are
    /// individual bits with fixed meanings and do not depend on the state table matching.
    /// </para>
    /// </summary>
    Unknown,
}

/// <summary>
/// A decoded CiA-402 statusword (object <c>0x6041</c>): the state machine's state plus the five
/// standard flag bits.
/// </summary>
/// <param name="State">
/// The state the drive reports, or <see cref="Cia402State.Unknown"/> for a word the state table does
/// not name.
/// </param>
/// <param name="VoltageEnabled">
/// Bit 4 — high voltage is applied to the drive. A drive can be in
/// <see cref="Cia402State.SwitchOnDisabled"/> with this clear simply because its DC bus is not
/// powered, which is the first thing to check when an enable sequence will not advance.
/// </param>
/// <param name="Warning">
/// Bit 7 — the drive is warning about something without faulting. What it means is
/// manufacturer-specific and usually needs a vendor object to explain.
/// </param>
/// <param name="Remote">
/// Bit 9 — the drive is taking commands from the fieldbus. When this is clear the drive is in local
/// control and a controlword write is accepted and then ignored, which is how a correct enable
/// sequence appears to do nothing at all.
/// </param>
/// <param name="TargetReached">
/// Bit 10 — the drive has arrived at its target (or has stopped, when a halt is commanded). Its
/// exact meaning depends on the mode of operation.
/// </param>
/// <param name="InternalLimitActive">
/// Bit 11 — an internal limit is restricting the drive: current, torque, velocity or a position
/// range, depending on the mode.
/// </param>
public readonly record struct Cia402Status(
    Cia402State State,
    bool VoltageEnabled,
    bool Warning,
    bool Remote,
    bool TargetReached,
    bool InternalLimitActive);

/// <summary>
/// The commands a CiA-402 controlword can carry.
///
/// <para>
/// Six members for the standard's seven table rows, and no <c>Unknown</c>: every one of the 65536
/// possible controlwords names exactly one of these, because every row has enough don't-care bits to
/// cover the domain between them. The missing seventh row is explained on <see cref="SwitchOn"/>.
/// </para>
/// </summary>
public enum Cia402Command
{
    /// <summary>
    /// Remove the enable-voltage request (bit 1 clear). Drops the drive to
    /// <see cref="Cia402State.SwitchOnDisabled"/> from anywhere.
    /// </summary>
    DisableVoltage,

    /// <summary>
    /// Quick stop (bit 1 set, bit 2 clear). Stops the drive by whatever means its quick stop option
    /// code (<c>0x605A</c>) selects.
    /// </summary>
    QuickStop,

    /// <summary>
    /// Shutdown — <c>0x0006</c>. Moves <see cref="Cia402State.SwitchOnDisabled"/> to
    /// <see cref="Cia402State.ReadyToSwitchOn"/>; the first word of the usual enable sequence.
    /// </summary>
    Shutdown,

    /// <summary>
    /// Switch on — <c>0x0007</c>. Moves <see cref="Cia402State.ReadyToSwitchOn"/> to
    /// <see cref="Cia402State.SwitchedOn"/>.
    ///
    /// <para>
    /// <b>This is also the Disable-operation command.</b> The standard lists them as two rows with
    /// the IDENTICAL bit pattern <c>0xxx0111</c> — transition 3 from Ready to switch on, and
    /// transition 5 back from Operation enabled — so which one a drive performs depends entirely on
    /// the state it is in when the word arrives. A decoder handed one word cannot tell them apart,
    /// and a second enum member would be a distinction this type cannot actually make: it would
    /// encode to the same word and could never be decoded back. So the word decodes to this member
    /// in both cases, and the state the drive is in tells you which transition it caused.
    /// </para>
    /// </summary>
    SwitchOn,

    /// <summary>
    /// Enable operation — <c>0x000F</c>. Moves <see cref="Cia402State.SwitchedOn"/> to
    /// <see cref="Cia402State.OperationEnabled"/>, the only state in which the drive follows a
    /// setpoint. Also the standard's "Switch on + enable operation", which is the same word.
    /// </summary>
    EnableOperation,

    /// <summary>
    /// Fault reset (bit 7 set) — clears a latched fault, if its cause has gone.
    ///
    /// <para>
    /// The standard makes this the RISING EDGE of bit 7, not the level, so holding <c>0x0080</c>
    /// resets a fault once and then does nothing. A caller must take the bit low again before it can
    /// reset a second fault. Nothing here can see an edge — this decodes a single word — so a word
    /// with bit 7 set reports this command whatever the low bits spell, which is exactly the
    /// precedence the standard's table gives it.
    /// </para>
    /// </summary>
    FaultReset,
}

/// <summary>
/// A decoded CiA-402 controlword (object <c>0x6040</c>).
/// </summary>
/// <param name="Command">The state machine command the word carries.</param>
/// <param name="Halt">
/// Bit 8 — halt. Orthogonal to <paramref name="Command"/>: it does not change which command the word
/// carries, it asks the drive to stop moving while staying in
/// <see cref="Cia402State.OperationEnabled"/>. What "stop" means is the drive's halt option code
/// (<c>0x605D</c>).
///
/// <para>
/// It is the flag most likely to explain a drive that is enabled, un-faulted, and stationary.
/// </para>
/// </param>
public readonly record struct Cia402Controlword(Cia402Command Command, bool Halt);

/// <summary>
/// The modes of operation CiA-402 defines, as written to <c>0x6060</c> and read back from
/// <c>0x6061</c>.
///
/// <para>
/// Backed by <see cref="sbyte"/> because both objects are SINT, so this enum cannot hold a value the
/// objects cannot carry. Values the standard leaves reserved or hands to the manufacturer are
/// deliberately absent — see <see cref="Cia402.DecodeModeOfOperation"/>.
/// </para>
/// </summary>
public enum Cia402Mode : sbyte
{
    /// <summary>No mode selected. A drive in this mode follows no setpoint even when enabled.</summary>
    NoMode = 0,

    /// <summary>Profile Position (pp) — move to a target position along a generated profile.</summary>
    ProfilePosition = 1,

    /// <summary>
    /// Velocity (vl) — the frequency-inverter velocity mode, driven by <c>0x6042</c>. Distinct from
    /// <see cref="ProfileVelocity"/>, and the one a general-purpose AC drive is most likely to offer.
    /// </summary>
    Velocity = 2,

    /// <summary>Profile Velocity (pv) — servo velocity control, driven by <c>0x60FF</c>.</summary>
    ProfileVelocity = 3,

    /// <summary>Profile Torque (tq) — torque control, driven by <c>0x6071</c>.</summary>
    ProfileTorque = 4,

    /// <summary>Homing (hm) — run the drive's homing method to establish its reference.</summary>
    Homing = 6,

    /// <summary>Interpolated Position (ip) — follow interpolated setpoints from the master.</summary>
    InterpolatedPosition = 7,

    /// <summary>
    /// Cyclic Synchronous Position (csp) — the master sends a position every cycle. The usual mode
    /// for a servo axis under a motion controller.
    /// </summary>
    CyclicSyncPosition = 8,

    /// <summary>Cyclic Synchronous Velocity (csv) — the master sends a velocity every cycle.</summary>
    CyclicSyncVelocity = 9,

    /// <summary>Cyclic Synchronous Torque (cst) — the master sends a torque every cycle.</summary>
    CyclicSyncTorque = 10,

    /// <summary>
    /// Cyclic Synchronous Torque with Commutation Angle (cstca) — as <see cref="CyclicSyncTorque"/>,
    /// with the commutation angle supplied by the master as well.
    /// </summary>
    CyclicSyncTorqueWithCommutationAngle = 11,
}
