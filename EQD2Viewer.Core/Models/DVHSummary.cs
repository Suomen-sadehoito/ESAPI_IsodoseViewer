namespace ESAPI_EQD2Viewer.Core.Models
{
    public class DVHSummary
    {
        public string StructureId { get; set; }
        public string PlanId { get; set; }
        public string Type { get; set; }
        public double DMax { get; set; }
        public double DMean { get; set; }
        public double DMin { get; set; }
        public double Volume { get; set; }
    }
}
