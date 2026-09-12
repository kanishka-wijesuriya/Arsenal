namespace Arsenal.Application.Models
{
    /// <summary>
    /// The inclusive span a control may offer, taken from what this machine will
    /// actually accept rather than from a number typed into a view.
    /// </summary>
    /// <remarks>
    /// Every writer below the UI silently drops a value outside its register's range -
    /// <c>ModeControl.SetPower</c> returns early, <c>SetGPUPower</c> skips the write.
    /// A slider whose track cannot reach the value the machine is running at is the
    /// visible half of that: the readout says one thing, the hardware another, and
    /// pressing Apply writes back whatever the track could represent. Carrying the
    /// real bounds up to the view keeps both halves honest.
    /// </remarks>
    public readonly record struct ControlRange(int Minimum, int Maximum)
    {
        /// <summary>Brings a stored or measured value onto the track.</summary>
        public int Clamp(int value) => value < Minimum ? Minimum : value > Maximum ? Maximum : value;

        /// <summary>False when the bounds collapsed, which means nothing to show.</summary>
        public bool IsUsable => Maximum > Minimum;
    }
}
