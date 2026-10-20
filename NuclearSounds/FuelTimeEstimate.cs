namespace NuclearSounds
{
    // No game dependencies: callers supply the actual time between samples.
    internal static class FuelTimeEstimate
    {
        internal static float Calculate(float previousFuel, float currentFuel, float elapsedSeconds)
        {
            if (!float.IsFinite(previousFuel) || !float.IsFinite(currentFuel)
                || !float.IsFinite(elapsedSeconds) || elapsedSeconds <= 0f
                || currentFuel < 0f || previousFuel <= currentFuel)
            {
                return float.PositiveInfinity;
            }

            return currentFuel / ((previousFuel - currentFuel) / elapsedSeconds);
        }
    }
}
