namespace EQD2Viewer.Core.Models
{
    public class RenderResult
    {
        public byte[] PixelData { get; set; }
        public string DiagnosticMessage { get; set; }
 public bool IsSuccessful => PixelData != null && PixelData.Length > 0;
    }
}
