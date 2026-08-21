using System;

namespace OrderWatchLite.Helpers
{
    public static class LotSizeHelper
    {
        public static double RoundLot(double raw, double step, double minLot, double maxLot)
        {
            if (step <= 0) step = 0.01;
            if (raw <= 0) return Math.Max(minLot, 0.01);

            double remainder = raw % step;
            double lot;
            if (remainder >= 0.7 * step)
                lot = Math.Ceiling(raw / step) * step;
            else
                lot = Math.Floor(raw / step) * step;

            lot = Math.Max(minLot, Math.Min(maxLot, lot));
            return lot;
        }
    }
}