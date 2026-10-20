using System;

namespace NuclearDelivery
{
    internal static class DeliveryLayout
    {
        // Convert camera coordinates into radar coordinates before applying the user's size multiplier.
        internal static float CockpitScale(float feedHeightInRadarUnits, float cameraHeight, float cameraScale, float cockpitScale)
        {
            if (!Positive(feedHeightInRadarUnits) || !Positive(cameraHeight)) return Setting(cockpitScale);
            return feedHeightInRadarUnits / cameraHeight * Setting(cameraScale) * Setting(cockpitScale);
        }
        private static bool Positive(float value) => value > 0 && !float.IsNaN(value) && !float.IsInfinity(value);
        private static float Setting(float value) => Math.Min(3, Math.Max(.25f, Positive(value) ? value : 1));
    }
}
