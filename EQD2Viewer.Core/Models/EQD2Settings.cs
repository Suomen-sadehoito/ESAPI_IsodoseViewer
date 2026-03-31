namespace ESAPI_EQD2Viewer.Core.Models
{
    public class EQD2Settings
    {
        public bool IsEnabled { get; set; }
        public double AlphaBeta { get; set; } = 3.0;
        public int NumberOfFractions { get; set; } = 1;
    }
}
