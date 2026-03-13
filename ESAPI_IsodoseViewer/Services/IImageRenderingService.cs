using System.Windows.Media.Imaging;
using VMS.TPS.Common.Model.API;

namespace ESAPI_IsodoseViewer.Services
{
    public interface IImageRenderingService
    {
        void Initialize(int width, int height);
        void RenderCtImage(Image ctImage, WriteableBitmap targetBitmap, int currentSlice, double windowLevel, double windowWidth);
        string RenderDoseImage(Image ctImage, Dose dose, WriteableBitmap targetBitmap, int currentSlice, double planTotalDose, double planNormalization);
    }
}