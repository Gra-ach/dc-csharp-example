namespace Gaia
{
    /// <summary>
    /// Persistent event imported into InterSystems IRIS by XEP.
    ///
    /// The resulting IRIS class name is:
    /// Gaia.EpochPhotometry
    ///
    /// It is normally projected to SQL as:
    /// Gaia.EpochPhotometry
    /// </summary>
    public sealed class EpochPhotometry
    {
        public long source_id;

        // Stored calculated fields.
        public double bp_min_flux;
        public double bp_max_flux;
        public double rp_min_flux;
        public double rp_max_flux;

        internal EpochPhotometry(
            long sourceId,
            double[] bpFlux,
            double[] rpFlux)
        {
            source_id = sourceId;

            bp_min_flux = GetMinimum(bpFlux);
            bp_max_flux = GetMaximum(bpFlux);
            rp_min_flux = GetMinimum(rpFlux);
            rp_max_flux = GetMaximum(rpFlux);
        }

        private static double GetMinimum(double[] values)
        {
            if (values == null || values.Length == 0)
            {
                return 0;
            }

            double minimum = double.PositiveInfinity;
            bool found = false;

            foreach (double value in values)
            {
                if (!double.IsFinite(value))
                {
                    continue;
                }

                if (value < minimum)
                {
                    minimum = value;
                    found = true;
                }
            }

            return found ? minimum : 0;
        }

        private static double GetMaximum(double[] values)
        {
            if (values == null || values.Length == 0)
            {
                return 0;
            }

            double maximum = double.NegativeInfinity;
            bool found = false;

            foreach (double value in values)
            {
                if (!double.IsFinite(value))
                {
                    continue;
                }

                if (value > maximum)
                {
                    maximum = value;
                    found = true;
                }
            }

            return found ? maximum : 0;
        }
    }
}