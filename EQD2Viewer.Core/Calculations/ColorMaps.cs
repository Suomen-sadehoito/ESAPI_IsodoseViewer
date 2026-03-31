namespace ESAPI_EQD2Viewer.Core.Calculations
{
    /// <summary>
    /// Shared color mapping functions used by both ImageRenderingService and summation rendering.
    /// Eliminates duplication of JetColormap / SumJet.
    /// </summary>
    public static class ColorMaps
    {
        /// <summary>
        /// Jet (rainbow) colormap: blue → cyan → green → yellow → red.
        /// Returns ARGB uint for direct pixel buffer writing.
        /// </summary>
        /// <param name="t">Normalized value [0, 1]</param>
        /// <param name="alpha">Alpha channel (0-255)</param>
        public static uint Jet(double t, byte alpha)
        {
            double r, g, b;

            if (t < 0.125)
            {
                r = 0; g = 0; b = 0.5 + t * 4.0;
            }
            else if (t < 0.375)
            {
                r = 0; g = (t - 0.125) * 4.0; b = 1.0;
            }
            else if (t < 0.625)
            {
                r = (t - 0.375) * 4.0; g = 1.0; b = 1.0 - (t - 0.375) * 4.0;
            }
            else if (t < 0.875)
            {
                r = 1.0; g = 1.0 - (t - 0.625) * 4.0; b = 0;
            }
            else
            {
                r = 1.0 - (t - 0.875) * 4.0; g = 0; b = 0;
            }

            byte rb = (byte)(Clamp01(r) * 255);
            byte gb = (byte)(Clamp01(g) * 255);
            byte bb = (byte)(Clamp01(b) * 255);

            return ((uint)alpha << 24) | ((uint)rb << 16) | ((uint)gb << 8) | bb;
        }

        /// <summary>
        /// Clamps value to [0, 1] range.
        /// </summary>
        public static double Clamp01(double v)
        {
            return v < 0 ? 0 : (v > 1 ? 1 : v);
        }
    }
}
