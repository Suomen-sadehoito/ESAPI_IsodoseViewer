namespace EQD2Viewer.Core.Models
{
    public class DoseVolumePoint
    {
        public double DoseGy { get; set; }
        public double VolumePercent { get; set; }
        public DoseVolumePoint(double doseGy, double volumePercent)
        {
            DoseGy = doseGy;
            VolumePercent = volumePercent;
        }
    }
}
