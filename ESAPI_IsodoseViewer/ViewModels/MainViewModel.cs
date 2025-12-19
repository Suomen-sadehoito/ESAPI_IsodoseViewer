using System;
using System.IO; // Lisätty StreamWriteria varten
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ESAPI_IsodoseViewer.Mvvm;
using VMS.TPS.Common.Model.API;
using VMS.TPS.Common.Model.Types;
using ESAPI_IsodoseViewer.Helpers; // Lisätty VVectorExtensions (Dot-metodi) varten

namespace ESAPI_IsodoseViewer.ViewModels
{
    public class MainViewModel : ObservableObject
    {
        private readonly ScriptContext _context;
        private readonly PlanSetup _plan;

        // Puskurit
        private int[,] _ctBuffer;
        private int _width, _height;
        private int _huOffset = 32768;

        // Properties for Binding
        private WriteableBitmap _ctImageSource;
        public WriteableBitmap CtImageSource
        {
            get => _ctImageSource;
            set => SetProperty(ref _ctImageSource, value);
        }

        private WriteableBitmap _doseImageSource;
        public WriteableBitmap DoseImageSource
        {
            get => _doseImageSource;
            set => SetProperty(ref _doseImageSource, value);
        }

        private int _currentSlice;
        public int CurrentSlice
        {
            get => _currentSlice;
            set
            {
                if (SetProperty(ref _currentSlice, value))
                {
                    RenderScene();
                }
            }
        }

        private int _maxSlice;
        public int MaxSlice
        {
            get => _maxSlice;
            set => SetProperty(ref _maxSlice, value);
        }

        private double _windowLevel;
        public double WindowLevel
        {
            get => _windowLevel;
            set
            {
                if (SetProperty(ref _windowLevel, value)) RenderScene();
            }
        }

        private double _windowWidth;
        public double WindowWidth
        {
            get => _windowWidth;
            set
            {
                if (SetProperty(ref _windowWidth, value)) RenderScene();
            }
        }

        private string _statusText;
        public string StatusText
        {
            get => _statusText;
            set => SetProperty(ref _statusText, value);
        }

        // Komennot
        public RelayCommand AutoPresetCommand { get; }
        public RelayCommand PresetCommand { get; }
        public RelayCommand DebugCommand { get; }

        public MainViewModel(ScriptContext context)
        {
            _context = context;
            _plan = context.ExternalPlanSetup;

            // Alustukset
            _width = _context.Image.XSize;
            _height = _context.Image.YSize;
            _maxSlice = _context.Image.ZSize - 1;
            _currentSlice = _maxSlice / 2;

            _ctBuffer = new int[_width, _height];

            // Luodaan kuvat
            CtImageSource = new WriteableBitmap(_width, _height, 96, 96, PixelFormats.Bgra32, null);
            DoseImageSource = new WriteableBitmap(_width, _height, 96, 96, PixelFormats.Bgra32, null);

            // Alusta komennot
            AutoPresetCommand = new RelayCommand(o => ExecuteAutoPreset());
            PresetCommand = new RelayCommand(param => ExecutePreset((string)param));
            DebugCommand = new RelayCommand(o => ExecuteDebug());

            // Aja alkutila
            ExecuteAutoPreset();
        }

        private void RenderScene()
        {
            RenderCtImage();
            RenderDoseImage();
        }

        private unsafe void RenderCtImage()
        {
            _context.Image.GetVoxels(_currentSlice, _ctBuffer);

            CtImageSource.Lock();
            byte* pBackBuffer = (byte*)CtImageSource.BackBuffer;
            int stride = CtImageSource.BackBufferStride;

            double level = _windowLevel;
            double width = _windowWidth;
            double huMin = level - (width / 2.0);
            double factor = 255.0 / width;

            for (int y = 0; y < _height; y++)
            {
                uint* pRow = (uint*)(pBackBuffer + y * stride);
                for (int x = 0; x < _width; x++)
                {
                    int hu = _ctBuffer[x, y] - _huOffset;
                    double valDouble = (hu - huMin) * factor;
                    byte val = (byte)(valDouble < 0 ? 0 : (valDouble > 255 ? 255 : valDouble));

                    // BGRA
                    pRow[x] = (0xFFu << 24) | ((uint)val << 16) | ((uint)val << 8) | val;
                }
            }

            CtImageSource.AddDirtyRect(new Int32Rect(0, 0, _width, _height));
            CtImageSource.Unlock();
        }

        private unsafe void RenderDoseImage()
        {
            // Tyhjennä annoskuva (täytä läpinäkyvällä)
            DoseImageSource.Lock();
            int doseStride = DoseImageSource.BackBufferStride;
            byte* pDoseBuffer = (byte*)DoseImageSource.BackBuffer;

            // Nollaa puskuri (MemorySet olisi nopeampi, mutta tämä on turvallinen tapa silmukassa)
            for (int i = 0; i < _height * doseStride; i++) pDoseBuffer[i] = 0;

            if (_plan?.Dose == null)
            {
                DoseImageSource.Unlock();
                return;
            }

            Dose dose = _plan.Dose;
            VMS.TPS.Common.Model.API.Image image = _context.Image;

            // -- Laskentalogiikka (kopioitu ja sovitettu alkuperäisestä) --
            double prescriptionGy = _plan.TotalDose.Dose;
            if (_plan.TotalDose.Unit == DoseValue.DoseUnit.cGy) prescriptionGy /= 100.0;

            double normalization = _plan.PlanNormalizationValue;
            if (double.IsNaN(normalization) || normalization <= 0) normalization = 100.0;
            else if (normalization < 5.0) normalization *= 100.0;

            double referenceDoseGy = prescriptionGy * (normalization / 100.0);
            if (referenceDoseGy < 0.1) referenceDoseGy = prescriptionGy;

            // Dose Z haku
            VVector ctPlaneCenterWorld = image.Origin + image.ZDirection * (_currentSlice * image.ZRes);
            VVector relativeToDoseOrigin = ctPlaneCenterWorld - dose.Origin;
            int doseSlice = (int)Math.Round(relativeToDoseOrigin.Dot(dose.ZDirection) / dose.ZRes);

            if (doseSlice < 0 || doseSlice >= dose.ZSize)
            {
                StatusText = $"CT Z: {_currentSlice} | Dose Z: {doseSlice} (Out of range)";
                DoseImageSource.Unlock();
                return;
            }

            // Dose Buffer haku
            int dx = dose.XSize;
            int dy = dose.YSize;
            int[,] doseBuffer = new int[dx, dy];
            dose.GetVoxels(doseSlice, doseBuffer);

            // Muunnoskertoimet
            DoseValue dv0 = dose.VoxelToDoseValue(0);
            DoseValue dvRef = dose.VoxelToDoseValue(10000);
            double rawScale = (dvRef.Dose - dv0.Dose) / 10000.0;
            double rawOffset = dv0.Dose;
            double unitToGyFactor = (dvRef.Unit == DoseValue.DoseUnit.Percent) ? prescriptionGy / 100.0 :
                                    (dvRef.Unit == DoseValue.DoseUnit.cGy) ? 0.01 : 1.0;

            // Väritasot
            var levels = new[] {
                new { Pct = 1.07, Color = 0xFFFF0000 }, // Red (ARGB)
                new { Pct = 0.95, Color = 0xFF00FF00 }, // Lime
                new { Pct = 0.80, Color = 0xFF00FFFF }, // Cyan
                new { Pct = 0.50, Color = 0xFF0000FF }  // Blue
            };

            double maxDoseInSlice = 0;
            double scaleX = dose.XRes / image.XRes;
            double scaleY = dose.YRes / image.YRes;

            // Renderöinti pikseleiksi (Mapping Dose grid -> Image grid)
            // Huom: Tämä on yksinkertaistettu "Nearest Neighbor" skaalaus suorituskyvyn vuoksi.

            for (int y = 0; y < dy; y++)
            {
                for (int x = 0; x < dx; x++)
                {
                    double valInUnits = doseBuffer[x, y] * rawScale + rawOffset;
                    double dGy = valInUnits * unitToGyFactor;
                    if (dGy > maxDoseInSlice) maxDoseInSlice = dGy;

                    uint color = 0;
                    // Tarkistetaan kynnykset
                    foreach (var level in levels)
                    {
                        if (dGy >= referenceDoseGy * level.Pct)
                        {
                            color = level.Color;
                            break; // Otetaan ylin väri
                        }
                    }

                    if (color != 0)
                    {
                        // Lisätään läpinäkyvyys (Alpha ~ 0.3 -> 0x4C)
                        // Korvataan täysi alpha (FF) arvolla 4C
                        color = (color & 0x00FFFFFF) | (0x4C000000);

                        // Piirretään skaalattu neliö CT-kuvan resoluutioon
                        // Laske Dose-pikselin keskipiste CT-pikseleinä
                        VVector worldPos = dose.Origin +
                                           dose.XDirection * (x * dose.XRes) +
                                           dose.YDirection * (y * dose.YRes) +
                                           dose.ZDirection * (doseSlice * dose.ZRes);

                        VVector diff = worldPos - image.Origin;
                        double px = diff.Dot(image.XDirection) / image.XRes;
                        double py = diff.Dot(image.YDirection) / image.YRes;

                        int startX = (int)(px - scaleX / 2.0);
                        int startY = (int)(py - scaleY / 2.0);
                        int endX = (int)(px + scaleX / 2.0);
                        int endY = (int)(py + scaleY / 2.0);

                        // Täytä alue WriteableBitmapissa
                        for (int py_img = startY; py_img < endY; py_img++)
                        {
                            if (py_img < 0 || py_img >= _height) continue;
                            uint* row = (uint*)(pDoseBuffer + py_img * doseStride);

                            for (int px_img = startX; px_img < endX; px_img++)
                            {
                                if (px_img >= 0 && px_img < _width)
                                {
                                    row[px_img] = color;
                                }
                            }
                        }
                    }
                }
            }

            StatusText = $"CT Z: {_currentSlice} | Dose Z: {doseSlice} | Max: {maxDoseInSlice:F2} Gy | Ref: {referenceDoseGy:F2} Gy";
            DoseImageSource.AddDirtyRect(new Int32Rect(0, 0, _width, _height));
            DoseImageSource.Unlock();
        }

        private void ExecuteAutoPreset()
        {
            _context.Image.GetVoxels(_currentSlice, _ctBuffer);
            int rawCenter = _ctBuffer[_width / 2, _height / 2];
            _huOffset = (rawCenter > 30000) ? 32768 : 0;

            // Aseta arvot ilman RenderScene-kutsua (estetään tuplarenderöinti)
            _windowLevel = rawCenter - _huOffset;
            _windowWidth = 400;

            // Pakota UI päivitys
            OnPropertyChanged(nameof(WindowLevel));
            OnPropertyChanged(nameof(WindowWidth));
            RenderScene();
        }

        private void ExecutePreset(string type)
        {
            switch (type)
            {
                case "Soft": WindowLevel = 40; WindowWidth = 400; break;
                case "Lung": WindowLevel = -600; WindowWidth = 1600; break;
                case "Bone": WindowLevel = 300; WindowWidth = 1500; break;
            }
        }

        private void ExecuteDebug()
        {
            try
            {
                string path = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "ESAPI_DeepDebug.txt");
                using (StreamWriter sw = new StreamWriter(path))
                {
                    sw.WriteLine("==================================================================================");
                    sw.WriteLine($"=== ESAPI MASSIVE DEBUG LOG - {DateTime.Now} ===");
                    sw.WriteLine("==================================================================================");

                    // 1. PERUSTIEDOT (CONTEXT)
                    sw.WriteLine("\n--- 1. PLAN & CONTEXT ---");
                    if (_plan == null) sw.WriteLine("PLAN IS NULL!");
                    else
                    {
                        sw.WriteLine($"Plan ID: {_plan.Id}");
                        sw.WriteLine($"Total Dose: {_plan.TotalDose.Dose} {_plan.TotalDose.Unit}");
                        sw.WriteLine($"Plan Normalization: {_plan.PlanNormalizationValue}%");
                        sw.WriteLine($"Dose Value presentation: {_plan.DoseValuePresentation}");
                    }

                    // 2. IMAGE METADATA
                    var image = _context.Image;
                    sw.WriteLine("\n--- 2. IMAGE GEOMETRY (CT) ---");
                    if (image == null)
                    {
                        sw.WriteLine("IMAGE IS NULL!");
                        return;
                    }
                    sw.WriteLine($"Size (X, Y, Z): {image.XSize}, {image.YSize}, {image.ZSize}");
                    sw.WriteLine($"Res (X, Y, Z):  {image.XRes:F4}, {image.YRes:F4}, {image.ZRes:F4} mm");
                    sw.WriteLine($"Origin (mm):    ({image.Origin.x:F2}, {image.Origin.y:F2}, {image.Origin.z:F2})");
                    sw.WriteLine($"X-Direction:    ({image.XDirection.x:F4}, {image.XDirection.y:F4}, {image.XDirection.z:F4})");
                    sw.WriteLine($"Y-Direction:    ({image.YDirection.x:F4}, {image.YDirection.y:F4}, {image.YDirection.z:F4})");
                    sw.WriteLine($"Z-Direction:    ({image.ZDirection.x:F4}, {image.ZDirection.y:F4}, {image.ZDirection.z:F4})");
                    sw.WriteLine($"DICOM Center (User Origin?): {image.UserOrigin.x:F2}, {image.UserOrigin.y:F2}, {image.UserOrigin.z:F2}");

                    // 3. DOSE METADATA
                    var dose = _plan.Dose;
                    sw.WriteLine("\n--- 3. DOSE GEOMETRY ---");
                    if (dose == null)
                    {
                        sw.WriteLine("DOSE IS NULL!");
                        return;
                    }
                    sw.WriteLine($"Size (X, Y, Z): {dose.XSize}, {dose.YSize}, {dose.ZSize}");
                    sw.WriteLine($"Res (X, Y, Z):  {dose.XRes:F4}, {dose.YRes:F4}, {dose.ZRes:F4} mm");
                    sw.WriteLine($"Origin (mm):    ({dose.Origin.x:F2}, {dose.Origin.y:F2}, {dose.Origin.z:F2})");
                    sw.WriteLine($"X-Direction:    ({dose.XDirection.x:F4}, {dose.XDirection.y:F4}, {dose.XDirection.z:F4})");
                    sw.WriteLine($"Y-Direction:    ({dose.YDirection.x:F4}, {dose.YDirection.y:F4}, {dose.YDirection.z:F4})");
                    sw.WriteLine($"Z-Direction:    ({dose.ZDirection.x:F4}, {dose.ZDirection.y:F4}, {dose.ZDirection.z:F4})");

                    // 4. SCALING FACTOR CHECK
                    sw.WriteLine("\n--- 4. SCALING FACTORS (Raw Int -> Physical Gy) ---");
                    DoseValue dv0 = dose.VoxelToDoseValue(0);
                    DoseValue dv10k = dose.VoxelToDoseValue(10000);

                    // Logataan mitä ESAPI palauttaa suoraan
                    sw.WriteLine($"Voxel(0)      -> ESAPI: {dv0.Dose} {dv0.Unit}");
                    sw.WriteLine($"Voxel(10000) -> ESAPI: {dv10k.Dose} {dv10k.Unit}");

                    double gy0 = (dv0.Unit == DoseValue.DoseUnit.cGy) ? dv0.Dose / 100.0 : dv0.Dose;
                    double gyRef = (dv10k.Unit == DoseValue.DoseUnit.cGy) ? dv10k.Dose / 100.0 : dv10k.Dose;

                    double dScale = (gyRef - gy0) / 10000.0;
                    sw.WriteLine($"Calculated Offset (Gy): {gy0}");
                    sw.WriteLine($"Calculated Scale (Gy/RawUnit): {dScale:E8}");

                    // 5. CURRENT SLICE MAPPING
                    sw.WriteLine("\n--- 5. SLICE MAPPING (Current View) ---");
                    int currentCtSlice = _currentSlice;
                    sw.WriteLine($"Current CT Slice Index: {currentCtSlice}");

                    // Laske Z-koordinaatti
                    VVector ctPlaneCenterWorld = image.Origin + image.ZDirection * (currentCtSlice * image.ZRes);
                    sw.WriteLine($"CT Slice Z World Pos: {ctPlaneCenterWorld.z:F2} mm");

                    // Miten tämä mappautuu Dose-matriisiin?
                    VVector relativeToDoseOrigin = ctPlaneCenterWorld - dose.Origin;
                    double zDiff = relativeToDoseOrigin.Dot(dose.ZDirection);
                    double doseSliceDouble = zDiff / dose.ZRes;
                    int doseSliceIndex = (int)Math.Round(doseSliceDouble);

                    sw.WriteLine($"Diff from Dose Origin Z: {zDiff:F2} mm");
                    sw.WriteLine($"Calculated Dose Slice Index (Double): {doseSliceDouble:F4}");
                    sw.WriteLine($"Calculated Dose Slice Index (Int):    {doseSliceIndex}");

                    if (doseSliceIndex < 0 || doseSliceIndex >= dose.ZSize)
                    {
                        sw.WriteLine("!!! VAROITUS: Dose Slice Index on matriisin ulkopuolella !!!");
                    }
                    else
                    {
                        // 6. LINE PROFILE DUMP
                        // Otetaan "näyte" keskeltä matriisia X-akselin suuntaisesti nähdäksesi arvot
                        sw.WriteLine("\n--- 6. CENTRAL AXIS X-PROFILE (Dose Matrix) ---");
                        sw.WriteLine("Format: [X-Index] | RawValue | CalculatedGy | WorldX (mm)");

                        int centerY = dose.YSize / 2;
                        int[,] buffer = new int[dose.XSize, dose.YSize];
                        dose.GetVoxels(doseSliceIndex, buffer);

                        // Dumppaa joka 10. pikseli jottei logi räjähdä, mutta näet trendin
                        for (int x = 0; x < dose.XSize; x += 5)
                        {
                            int raw = buffer[x, centerY];
                            double valGy = raw * dScale + gy0;

                            // Laske tämän pikselin maailmankoordinaatti
                            VVector pixelWorldPos = dose.Origin + dose.XDirection * (x * dose.XRes) + dose.YDirection * (centerY * dose.YRes) + dose.ZDirection * (doseSliceIndex * dose.ZRes);

                            // Kirjoita vain jos arvo ei ole nolla (säästää tilaa) tai jos se on nolla, kirjoita harvemmin
                            if (valGy > 0.05 || x % 20 == 0)
                            {
                                sw.WriteLine($"[{x,3}] | {raw,6} | {valGy,6:F3} Gy | X: {pixelWorldPos.x,6:F1}");
                            }
                        }
                    }
                }
                MessageBox.Show($"Debug-raportti luotu: {path}\n\nTarkista tiedostosta erityisesti:\n1. Dose Res vs Image Res\n2. Dose Origin vs Image Origin\n3. Calculated Gy arvot (ovatko ne järkeviä?)");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Virhe debug-tallennuksessa: {ex.ToString()}");
            }
        }
    }
}