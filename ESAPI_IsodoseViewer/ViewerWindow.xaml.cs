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
    /// <summary>
    /// Ikkuna CT-kuvan ja annosjakauman (Color Wash) katseluun.
    /// </summary>
    public partial class ViewerWindow : Window
    {
        private readonly ScriptContext _context;
        private readonly PlanSetup _plan;

        private WriteableBitmap _ctBitmap;
        private int _width, _height;
        private int[,] _ctBuffer;

        private bool _isRendering = false;
        private bool _initialized = false;

        // Oletusarvo Hounsfield-yksiköiden korjaukselle (unsigned -> signed muunnos)
        private int _huOffset = 32768;

        public ViewerWindow(ScriptContext context)
        {
            _context = context;
            _plan = context.ExternalPlanSetup;

            InitializeComponent();

            // Varmistetaan, että tarvittavat tiedot ovat saatavilla
            if (_context.Image == null || _plan == null || _plan.Dose == null)
            {
                MessageBox.Show("Virhe: Suunnitelmaa, kuvaa tai annosta ei löytynyt contextista.", "Virhe", MessageBoxButton.OK, MessageBoxImage.Error);
                Close();
                return;
            }

            InitializeBitmap();
            SetupSlider();

            _initialized = true;

            // Asetetaan oletusikkunointi ja renderöidään ensimmäinen näkymä
            Preset_Auto(null, null);
        }

        /// <summary>
        /// Alustaa kuvapuskurit ja UI-komponentit vastaamaan CT-kuvan resoluutiota.
        /// </summary>
        private void InitializeBitmap()
        {
            _width = _context.Image.XSize;
            _height = _context.Image.YSize;
            _ctBuffer = new int[_width, _height];

            // Asetetaan Canvas ja Image -kontrollit vastaamaan CT-kuvan pikselikokoa
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

        /// <summary>
        /// Päärenderöintilogiikka. Päivittää sekä CT-kuvan että annoskerroksen.
        /// </summary>
        private void RenderSlice(int sliceIndex)
        {
            if (!_initialized || _isRendering) return;
            _isRendering = true;

            try
            {
                // 1. Renderöidään CT-kuva taustalle
                RenderCtImage(sliceIndex);

                // 2. Renderöidään annoskerros (Color Wash) päälle
                DrawColorWashDose(sliceIndex);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Renderöintivirhe: {ex.Message}");
            }
            finally
            {
                _isRendering = false;
            }
        }

        /// <summary>
        /// Hakee CT-pikselit ja piirtää ne WriteableBitmap-objektiin harmaasävykuvana.
        /// </summary>
        private void RenderCtImage(int sliceIndex)
        {
            _context.Image.GetVoxels(sliceIndex, _ctBuffer);

            _ctBitmap.Lock();
            unsafe
            {
                byte* pBackBuffer = (byte*)_ctBitmap.BackBuffer;
                int stride = _ctBitmap.BackBufferStride;

                double level = LevelSlider.Value;
                double width = WidthSlider.Value;
                double huMin = level - (width / 2.0);

                // Optimointi: Lasketaan kerroin valmiiksi silmukan ulkopuolella
                double factor = 255.0 / width;

                for (int y = 0; y < _height; y++)
                {
                    uint* pRow = (uint*)(pBackBuffer + y * stride);
                    for (int x = 0; x < _width; x++)
                    {
                        // Muunnetaan raaka pikseliarvo Hounsfield-yksiköksi
                        int hu = _ctBuffer[x, y] - _huOffset;

                        // Ikkunointilaskenta (Window/Level)
                        double valDouble = (hu - huMin) * factor;
                        byte val = (byte)(valDouble < 0 ? 0 : (valDouble > 255 ? 255 : valDouble));

                        // Kirjoitetaan pikseli (Format: BGRA32)
                        pRow[x] = (0xFFu << 24) | ((uint)val << 16) | ((uint)val << 8) | val;
                    }
                }
            }
            _ctBitmap.AddDirtyRect(new Int32Rect(0, 0, _width, _height));
            _ctBitmap.Unlock();
        }

        /// <summary>
        /// Piirtää annosjakauman läpikuultavina värialueina (Color Wash).
        /// Korjaa aiemman "index 1 vs 10000" skaalausongelman.
        /// </summary>
        private void DrawColorWashDose(int ctSliceIndex)
        {
            IsodoseCanvas.Children.Clear();
            Dose dose = _plan.Dose;
            if (dose == null) return;

            VMS.TPS.Common.Model.API.Image image = _context.Image;

            // --- 1. Määritetään referenssiannos (100% taso) ---
            double prescriptionGy = _plan.TotalDose.Dose;
            if (_plan.TotalDose.Unit == DoseValue.DoseUnit.cGy)
                prescriptionGy /= 100.0;

            double normalization = _plan.PlanNormalizationValue;

            // Käsittele normalisoinnin poikkeustilanteet (ESAPI voi palauttaa NaN tai 1.0 prosenntien sijaan)
            if (double.IsNaN(normalization) || normalization <= 0) normalization = 100.0;
            else if (normalization < 5.0) normalization *= 100.0; // Oletetaan kertoimeksi, jos alle 5

            double referenceDoseGy = prescriptionGy * (normalization / 100.0);

            // Turvaverkko: jos laskettu referenssi on epärealistinen, käytetään reseptiä
            if (referenceDoseGy < 0.1) referenceDoseGy = prescriptionGy;

            // --- 2. Skaalauskertoimien laskenta (KRIITTINEN KORJAUS) ---
            // Käytetään suurta indeksiä (10000) lineaarisen kertoimen laskemiseen.
            // Tämä minimoi liukulukujen pyöristysvirheet, jotka aiheuttavat vääriä annosarvoja (esim. 102 Gy).
            DoseValue dv0 = dose.VoxelToDoseValue(0);
            DoseValue dvRef = dose.VoxelToDoseValue(10000);

            double gy0 = (dv0.Unit == DoseValue.DoseUnit.cGy) ? dv0.Dose / 100.0 : dv0.Dose;
            double gyRef = (dvRef.Unit == DoseValue.DoseUnit.cGy) ? dvRef.Dose / 100.0 : dvRef.Dose;

            double dOffset = gy0;
            double dScale = (gyRef - gy0) / 10000.0;

            // --- 3. Koordinaattimuunnos: CT Z -> Dose Z ---
            // Määritetään, mikä annosmatriisin leike vastaa nykyistä CT-leikettä.
            VVector ctPlaneCenterWorld = image.Origin + image.ZDirection * (ctSliceIndex * image.ZRes);
            VVector relativeToDoseOrigin = ctPlaneCenterWorld - dose.Origin;
            int doseSlice = (int)Math.Round(relativeToDoseOrigin.Dot(dose.ZDirection) / dose.ZRes);

            if (doseSlice < 0 || doseSlice >= dose.ZSize)
            {
                SliceInfo.Text = $"CT Z: {ctSliceIndex} | Dose Z: {doseSlice} (Ulkopuolella)";
                return;
            }

            // --- 4. Piirtologiikka (Color Wash) ---
            // Määritellään väritasot. Järjestys on merkitsevä (pienin ensin), jos piirretään päällekkäin.
            var levels = new[] {
                new { Pct = 0.50, Color = Colors.Blue },
                new { Pct = 0.80, Color = Colors.Cyan },
                new { Pct = 0.95, Color = Colors.Lime },
                new { Pct = 1.07, Color = Colors.Red } // Hotspot > 107%
            };

            int dx = dose.XSize;
            int dy = dose.YSize;
            int[,] doseBuffer = new int[dx, dy];
            dose.GetVoxels(doseSlice, doseBuffer);

            // Lasketaan skaalauspiirtoa varten: Yksi annospikseli voi vastata useaa CT-pikseliä
            double scaleX = dose.XRes / image.XRes;
            double scaleY = dose.YRes / image.YRes;
            double maxDoseInSlice = 0;

            for (int y = 0; y < dy; y++)
            {
                for (int x = 0; x < dx; x++)
                {
                    // Muunnetaan raaka-arvo Grayksi korjatulla kertoimella
                    double dGy = doseBuffer[x, y] * dScale + dOffset;
                    if (dGy > maxDoseInSlice) maxDoseInSlice = dGy;

                    // Etsitään korkein kynnysarvo, jonka pikseli ylittää
                    Color? pixelColor = null;
                    for (int i = levels.Length - 1; i >= 0; i--)
                    {
                        if (dGy >= (referenceDoseGy * levels[i].Pct))
                        {
                            pixelColor = levels[i].Color;
                            break;
                        }
                    }

                    if (pixelColor.HasValue)
                    {
                        // Lasketaan pikselin sijainti maailmankoordinaatistossa ja projisoidaan CT-kuvalle
                        VVector worldPos = dose.Origin +
                                           dose.XDirection * (x * dose.XRes) +
                                           dose.YDirection * (y * dose.YRes) +
                                           dose.ZDirection * (doseSlice * dose.ZRes);

                        VVector diff = worldPos - image.Origin;

                        // Dot-tulo huomioi mahdolliset rotaatiot
                        double px = diff.Dot(image.XDirection) / image.XRes;
                        double py = diff.Dot(image.YDirection) / image.YRes;

                        // Tarkistetaan, että pikseli on kankaan alueella (huomioiden skaalauksen)
                        if (px >= -scaleX && px < _width && py >= -scaleY && py < _height)
                        {
                            Rectangle rect = new Rectangle
                            {
                                Width = scaleX,
                                Height = scaleY,
                                Fill = new SolidColorBrush(pixelColor.Value) { Opacity = 0.3 }, // 30% peittävyys
                                IsHitTestVisible = false // Optimointi: ei hiiritapahtumia
                            };

                            // Keskitetään suorakulmio laskettuun pisteeseen
                            Canvas.SetLeft(rect, px - (scaleX / 2));
                            Canvas.SetTop(rect, py - (scaleY / 2));

                            IsodoseCanvas.Children.Add(rect);
                        }
                    }
                }
            }

            SliceInfo.Text = $"CT Z: {ctSliceIndex} | Dose Z: {doseSlice} | Max: {maxDoseInSlice:F2} Gy | Ref 100%: {referenceDoseGy:F2} Gy";
        }

        // --- TAPAHTUMAKÄSITTELIJÄT (EVENT HANDLERS) ---

        private void SliceSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_initialized) RenderSlice((int)e.NewValue);
        }

        private void Windowing_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_initialized) RenderSlice((int)SliceSlider.Value);
        }

        /// <summary>
        /// Asettaa ikkunoinnin automaattisesti kuvan keskipisteen perusteella.
        /// </summary>
        private void Preset_Auto(object sender, RoutedEventArgs e)
        {
            if (_width == 0 || _height == 0) return;

            // Haetaan nykyinen leike puskuriin analyysia varten
            _context.Image.GetVoxels((int)SliceSlider.Value, _ctBuffer);

            int rawCenter = _ctBuffer[_width / 2, _height / 2];

            // Tunnistetaan, onko kuva tallennettu unsigned integerinä (jolloin offset on tarpeen)
            // Tyypillisesti CT-kuvassa ilma on -1000 HU. Jos raaka-arvo on > 30000, se on siirretty.
            _huOffset = (rawCenter > 30000) ? 32768 : 0;

            // Asetetaan oletusarvot (Level = keskipiste, Width = 1000)
            SetWL(rawCenter - _huOffset, 1000);

            // Pakotetaan uudelleenpiirto, jos kutsu tuli napista
            if (sender != null) RenderSlice((int)SliceSlider.Value);
        }

        private void SetWL(int level, int width)
        {
            if (LevelSlider != null)
            {
                LevelSlider.Value = level;
                WidthSlider.Value = width;
            }
        }

        // Esiasetukset eri kudostyypeille
        private void Preset_SoftTissue(object sender, RoutedEventArgs e)
        {
            SetWL(40, 400);
            RenderSlice((int)SliceSlider.Value);
        }

        private void Preset_Lung(object sender, RoutedEventArgs e)
        {
            SetWL(-600, 1600);
            RenderSlice((int)SliceSlider.Value);
        }

        private void Preset_Bone(object sender, RoutedEventArgs e)
        {
            SetWL(300, 1500);
            RenderSlice((int)SliceSlider.Value);
        }
        private void ExportDebugInfo(object sender, RoutedEventArgs e)
        {
            try
            {
                string path = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "ESAPI_DeepDebug.txt");
                using (StreamWriter sw = new StreamWriter(path))
                {
                    sw.WriteLine("=== ESAPI DEBUG SNAPSHOT ===");
                    sw.WriteLine($"Time: {DateTime.Now}");

                    if (_context.Image != null)
                    {
                        sw.WriteLine($"Image ID: {_context.Image.Id}");
                        sw.WriteLine($"Image Size: {_context.Image.XSize}, {_context.Image.YSize}, {_context.Image.ZSize}");
                        sw.WriteLine($"Image Res: {_context.Image.XRes:F4}, {_context.Image.YRes:F4}, {_context.Image.ZRes:F4}");
                    }

                    if (_plan.Dose != null)
                    {
                        sw.WriteLine($"Dose ID: {_plan.Dose.Id}");
                        sw.WriteLine($"Dose Size: {_plan.Dose.XSize}, {_plan.Dose.YSize}, {_plan.Dose.ZSize}");
                        sw.WriteLine($"Dose Res: {_plan.Dose.XRes:F4}, {_plan.Dose.YRes:F4}, {_plan.Dose.ZRes:F4}");

                        // Tarkistetaan skaalauskertoimet
                        DoseValue dv0 = _plan.Dose.VoxelToDoseValue(0);
                        DoseValue dvRef = _plan.Dose.VoxelToDoseValue(10000);

                        double gy0 = (dv0.Unit == DoseValue.DoseUnit.cGy) ? dv0.Dose / 100.0 : dv0.Dose;
                        double gyRef = (dvRef.Unit == DoseValue.DoseUnit.cGy) ? dvRef.Dose / 100.0 : dvRef.Dose;
                        double dScale = (gyRef - gy0) / 10000.0;

                        sw.WriteLine($"Calculated Scaling Factor: {dScale:E6} Gy/unit");
                        sw.WriteLine($"Offset: {gy0:F6} Gy");
                    }
                }
                MessageBox.Show($"Debug-tiedot tallennettu: {path}");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Virhe tallennuksessa: {ex.Message}");
            }
        }
    }
}