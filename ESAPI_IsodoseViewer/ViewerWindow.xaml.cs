using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Controls;
using System.Windows.Shapes;
using VMS.TPS.Common.Model.API;
using VMS.TPS.Common.Model.Types;

namespace ESAPI_IsodoseViewer
{
    public partial class ViewerWindow : Window
    {
        private ScriptContext _context;
        private PlanSetup _plan;
        private WriteableBitmap _ctBitmap;
        private int _width, _height;
        private int[,] _ctBuffer;
        private bool _isRendering = false;
        private bool _initialized = false;
        private int _huOffset = 32768;

        public ViewerWindow(ScriptContext context)
        {
            _context = context;
            _plan = context.ExternalPlanSetup;

            InitializeComponent();

            if (_context.Image == null || _plan == null || _plan.Dose == null)
            {
                MessageBox.Show("Virhe: Varmista että kuva ja annos on ladattu.");
                return;
            }

            InitializeBitmap();
            SetupSlider();

            _initialized = true;
            Preset_Auto(null, null);
            RenderSlice((int)SliceSlider.Value);
        }

        private void InitializeBitmap()
        {
            _width = _context.Image.XSize;
            _height = _context.Image.YSize;
            _ctBuffer = new int[_width, _height];

            // Asetetaan Canvas ja Image tismalleen CT-kuvan kokoon (pikseleinä)
            ImageContainer.Width = _width;
            ImageContainer.Height = _height;
            CtImageControl.Width = _width;
            CtImageControl.Height = _height;
            IsodoseCanvas.Width = _width;
            IsodoseCanvas.Height = _height;

            _ctBitmap = new WriteableBitmap(_width, _height, 96, 96, PixelFormats.Bgra32, null);
            CtImageControl.Source = _ctBitmap;
        }

        private void SetupSlider()
        {
            SliceSlider.Minimum = 0;
            SliceSlider.Maximum = _context.Image.ZSize - 1;
            SliceSlider.Value = _context.Image.ZSize / 2;
        }

        private void RenderSlice(int sliceIndex)
        {
            if (!_initialized || _isRendering) return;
            _isRendering = true;

            try
            {
                // Haetaan CT-pikselit
                _context.Image.GetVoxels(sliceIndex, _ctBuffer);

                _ctBitmap.Lock();
                unsafe
                {
                    byte* pBackBuffer = (byte*)_ctBitmap.BackBuffer;
                    int stride = _ctBitmap.BackBufferStride;
                    double level = LevelSlider.Value;
                    double width = WidthSlider.Value;
                    double huMin = level - (width / 2.0);
                    double factor = 255.0 / width;

                    for (int y = 0; y < _height; y++)
                    {
                        uint* pRow = (uint*)(pBackBuffer + y * stride);
                        for (int x = 0; x < _width; x++)
                        {
                            // KORJAUS 1: Luetaan puskuria suoraan. 
                            // DICOM y=0 on Anterior (vatsa), WPF y=0 on yläreuna.
                            // Tämä asettaa vatsan ylös ja selän alas.
                            int hu = _ctBuffer[x, y] - _huOffset;

                            double res = (hu - huMin) * factor;
                            byte val = (byte)(res < 0 ? 0 : (res > 255 ? 255 : res));

                            // BGRA: Blue, Green, Red, Alpha
                            pRow[x] = (0xFFu << 24) | ((uint)val << 16) | ((uint)val << 8) | val;
                        }
                    }
                }
                _ctBitmap.AddDirtyRect(new Int32Rect(0, 0, _width, _height));
                _ctBitmap.Unlock();

                DrawRealIsodoses(sliceIndex);
            }
            catch (Exception ex) { MessageBox.Show("Renderöintivirhe: " + ex.Message); }
            finally { _isRendering = false; }
        }

        private void DrawRealIsodoses(int ctSliceIndex)
        {
            IsodoseCanvas.Children.Clear();
            Dose dose = _plan.Dose;
            VMS.TPS.Common.Model.API.Image image = _context.Image;

            // 1. ANNOKSEN SKAALAUS (Gy)
            double prescriptionGy = _plan.TotalDose.Dose;
            if (_plan.TotalDose.Unit == DoseValue.DoseUnit.cGy) prescriptionGy /= 100.0;

            double normalization = _plan.PlanNormalizationValue;
            if (double.IsNaN(normalization) || normalization <= 0) normalization = 100.0;
            double referenceDoseGy = prescriptionGy * (normalization / 100.0);

            // Annos-voxelin muunnos Gy:ksi (VoxelToDoseValue on usein hidas silmukassa, lasketaan kertoimet)
            DoseValue dv0 = dose.VoxelToDoseValue(0);
            DoseValue dv1 = dose.VoxelToDoseValue(1);
            double dOffset = (dv0.Unit == DoseValue.DoseUnit.cGy) ? dv0.Dose / 100.0 : dv0.Dose;
            double dScale = ((dv1.Unit == DoseValue.DoseUnit.cGy) ? dv1.Dose / 100.0 : dv1.Dose) - dOffset;

            // 2. MATEMAATTINEN Z-TASO
            // Selvitetään mikä annosmatriisin taso vastaa nykyistä CT-leikettä
            VVector ctPlaneCenterWorld = image.Origin + image.ZDirection * (ctSliceIndex * image.ZRes);
            VVector relativeToDoseOrigin = ctPlaneCenterWorld - dose.Origin;
            int doseSlice = (int)Math.Round(relativeToDoseOrigin.Dot(dose.ZDirection) / dose.ZRes);

            if (doseSlice < 0 || doseSlice >= dose.ZSize) return;

            int dx = dose.XSize;
            int dy = dose.YSize;
            int[,] doseBuffer = new int[dx, dy];
            dose.GetVoxels(doseSlice, doseBuffer);

            // Isodoositasot (110%, 100%, 95%, 80%, 50%)
            double[] pctLevels = { 1.10, 1.00, 0.95, 0.80, 0.50 };
            Brush[] colors = { Brushes.Magenta, Brushes.Orange, Brushes.Lime, Brushes.Cyan, Brushes.Blue };

            // 3. PIIRTÄMINEN
            for (int y = 0; y < dy; y++)
            {
                for (int x = 0; x < dx; x++)
                {
                    double dGy = doseBuffer[x, y] * dScale + dOffset;

                    for (int i = 0; i < pctLevels.Length; i++)
                    {
                        double targetGy = referenceDoseGy * pctLevels[i];

                        // Piirretään "piste" jos voxelin annos on lähellä isodoosirajaa
                        if (Math.Abs(dGy - targetGy) < (referenceDoseGy * 0.006))
                        {
                            // LASKENTA: Annos-voxelin maailmankoordinaatit
                            VVector worldPos = dose.Origin +
                                             dose.XDirection * (x * dose.XRes) +
                                             dose.YDirection * (y * dose.YRes) +
                                             dose.ZDirection * (doseSlice * dose.ZRes);

                            // LASKENTA: Muunnos CT-pikselikoordinaateiksi
                            VVector diff = worldPos - image.Origin;
                            double px = diff.Dot(image.XDirection) / image.XRes;
                            double py = diff.Dot(image.YDirection) / image.YRes;

                            // KORJAUS 2: Ei enää peilausta tässäkään.
                            // py=0 on Anterior (vatsa), joka vastaa nyt kuvan yläreunaa.
                            double canvasX = px;
                            double canvasY = py;

                            if (canvasX >= 0 && canvasX < _width && canvasY >= 0 && canvasY < _height)
                            {
                                Rectangle dot = new Rectangle { Width = 1.5, Height = 1.5, Fill = colors[i] };
                                Canvas.SetLeft(dot, canvasX - 0.75);
                                Canvas.SetTop(dot, canvasY - 0.75);
                                IsodoseCanvas.Children.Add(dot);
                            }
                        }
                    }
                }
            }

            SliceInfo.Text = $"CT Z: {ctSliceIndex} | Dose Z: {doseSlice} | Ref: {referenceDoseGy:F2} Gy | Pos: {ctPlaneCenterWorld.z:F1} mm";
        }

        // --- APUMETODIT ---

        private void SliceSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_initialized) RenderSlice((int)e.NewValue);
        }

        private void Windowing_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_initialized) RenderSlice((int)SliceSlider.Value);
        }

        private void Preset_Auto(object sender, RoutedEventArgs e)
        {
            _context.Image.GetVoxels((int)SliceSlider.Value, _ctBuffer);
            int rawCenter = _ctBuffer[_width / 2, _height / 2];
            _huOffset = (rawCenter > 20000) ? 32768 : 0;
            SetWL(rawCenter - _huOffset, 1000);
        }

        private void SetWL(int l, int w)
        {
            if (LevelSlider != null) { LevelSlider.Value = l; WidthSlider.Value = w; }
        }

        private void Preset_SoftTissue(object sender, RoutedEventArgs e) { SetWL(40, 400); RenderSlice((int)SliceSlider.Value); }
        private void Preset_Lung(object sender, RoutedEventArgs e) { SetWL(-600, 1600); RenderSlice((int)SliceSlider.Value); }
        private void Preset_Bone(object sender, RoutedEventArgs e) { SetWL(300, 1500); RenderSlice((int)SliceSlider.Value); }

        private void ExportDebugInfo(object sender, RoutedEventArgs e)
        {
            try
            {
                // KORJAUS: Lisätty System.IO. etuliite, jotta se ei sekoitu WPF:n Path-muotoon
                string path = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "ESAPI_Debug_Log.txt");

                using (StreamWriter sw = new StreamWriter(path))
                {
                    var img = _context.Image;
                    var d = _plan.Dose;
                    sw.WriteLine($"--- IMAGE INFO ---");
                    sw.WriteLine($"Resolution: {img.XRes:F4} x {img.YRes:F4} x {img.ZRes:F4}");
                    sw.WriteLine($"Origin: {img.Origin}");
                    sw.WriteLine($"Size: {img.XSize} x {img.YSize} x {img.ZSize}");
                    sw.WriteLine($"--- DOSE INFO ---");
                    sw.WriteLine($"Resolution: {d.XRes:F4} x {d.YRes:F4} x {d.ZRes:F4}");
                    sw.WriteLine($"Origin: {d.Origin}");
                    sw.WriteLine($"Size: {d.XSize} x {d.YSize} x {d.ZSize}");
                    sw.WriteLine($"--- PLAN INFO ---");
                    sw.WriteLine($"Prescription: {_plan.TotalDose}");
                    sw.WriteLine($"Normalization: {_plan.PlanNormalizationValue}");
                }
                MessageBox.Show("Debug-tiedot tallennettu työpöydälle.");
            }
            catch (Exception ex)
            {
                MessageBox.Show("Debug-virhe: " + ex.Message);
            }
        }
    }
}