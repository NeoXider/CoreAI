namespace CoreAI.Mods.Rbx.Instances.Scheduling
{
    /// <summary>Injected monotonic scaled-time source advanced by the scheduler's frame driver.</summary>
    public interface IRbxTimeSource
    {
        /// <summary>Current scaled scheduler time in seconds.</summary>
        double CurrentTime { get; }

        /// <summary>Advances scaled scheduler time from one driver-supplied frame delta.</summary>
        void Advance(double deltaSeconds);
    }

    /// <summary>Engine-free accumulated scaled-time source suitable for runtime composition.</summary>
    public sealed class RbxAccumulatingTimeSource : IRbxTimeSource
    {
        public RbxAccumulatingTimeSource(double initialTime = 0d)
        {
            if (double.IsNaN(initialTime) || double.IsInfinity(initialTime))
            {
                throw RbxError.BadArgument(
                    "RbxAccumulatingTimeSource initial time must be finite",
                    "pass a finite initial scaled time");
            }

            CurrentTime = initialTime;
        }

        /// <inheritdoc />
        public double CurrentTime { get; private set; }

        /// <inheritdoc />
        public void Advance(double deltaSeconds)
        {
            if (double.IsNaN(deltaSeconds) || double.IsInfinity(deltaSeconds) || deltaSeconds < 0d)
            {
                throw RbxError.BadArgument(
                    "scheduler deltaSeconds must be finite and non-negative",
                    "pass the scaled non-negative frame delta");
            }

            CurrentTime += deltaSeconds;
        }
    }
}
