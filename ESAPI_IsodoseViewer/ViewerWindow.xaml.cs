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
        /// Renderöi annosjakauman värikerroksena (Color Wash) CT-kuvan päälle.
        /// <br/>
        /// Laskenta huomioi:
        /// <list type="bullet">
        /// <item>Annosmatriisin ja CT-kuvan eriävät resoluutiot ja origot.</item>
        /// <item>Muunnoksen raaka-arvoista (int) fysikaaliseksi annokseksi (Gy), huomioiden esitystavan (Relative/Absolute).</item>
        /// <item>Suunnitelman normalisoinnin ja kokonaisannoksen.</item>
        /// </list>
        /// </summary>
        /// <param name="ctSliceIndex">Nykyisen CT-leikkeen indeksi (Z).</param>
        private void DrawColorWashDose(int ctSliceIndex)
        {
            IsodoseCanvas.Children.Clear();
            Dose dose = _plan.Dose;
            if (dose == null) return;

            VMS.TPS.Common.Model.API.Image image = _context.Image;

            // --------------------------------------------------------------------------------
            // 1. Määritetään referenssiannos (100% isodoositaso Grayna)
            // --------------------------------------------------------------------------------
            double prescriptionGy = _plan.TotalDose.Dose;

            // Varmistetaan, että resepti on Gray-yksiköissä laskentaa varten
            if (_plan.TotalDose.Unit == DoseValue.DoseUnit.cGy)
                prescriptionGy /= 100.0;

            double normalization = _plan.PlanNormalizationValue;

            // Robustisuustarkistus: Jos ESAPI palauttaa puuttuvan tai epäloogisen normalisoinnin
            if (double.IsNaN(normalization) || normalization <= 0)
                normalization = 100.0;
            else if (normalization < 5.0)
                normalization *= 100.0; // Oletus: arvo on annettu kertoimena (esim. 1.0) eikä prosentteina

            // Lasketaan 100% isodoosia vastaava fysikaalinen annos
            double referenceDoseGy = prescriptionGy * (normalization / 100.0);

            // Turvaverkko: Käytetään reseptiä suoraan, jos laskenta tuottaa epärealistisen pienen arvon
            if (referenceDoseGy < 0.1) referenceDoseGy = prescriptionGy;

            // --------------------------------------------------------------------------------
            // 2. Skaalauskertoimien laskenta (Raw Voxel Value -> Physical Dose)
            // --------------------------------------------------------------------------------
            // Käytetään laajaa väliä (0 vs 10000) lineaarisen kertoimen laskemiseen tarkkuuden maksimoimiseksi.
            DoseValue dv0 = dose.VoxelToDoseValue(0);
            DoseValue dvRef = dose.VoxelToDoseValue(10000);

            // Lasketaan kulmakerroin (slope) ja vakiotermi (intercept) ESAPI:n palauttamassa yksikössä
            double rawScale = (dvRef.Dose - dv0.Dose) / 10000.0;
            double rawOffset = dv0.Dose;

            // Määritetään muunnoskerroin ESAPI-yksiköstä (%, cGy, Gy) -> Gray (Gy)
            double unitToGyFactor = 1.0;

            if (dvRef.Unit == DoseValue.DoseUnit.Percent)
            {
                // RELATIVE: Arvot ovat prosentteja kokonaisannoksesta.
                // Kaava: (Prosentti / 100) * ReseptiGy
                unitToGyFactor = prescriptionGy / 100.0;
            }
            else if (dvRef.Unit == DoseValue.DoseUnit.cGy)
            {
                // ABSOLUTE cGy: Jaetaan sadalla
                unitToGyFactor = 0.01;
            }
            // Jos yksikkö on valmiiksi Gy, kerroin on 1.0

            // --------------------------------------------------------------------------------
            // 3. Koordinaattimuunnos: CT Z -> Dose Z
            // --------------------------------------------------------------------------------
            // Lasketaan CT-leikkeen keskipisteen Z-koordinaatti maailmankoordinaatistossa
            VVector ctPlaneCenterWorld = image.Origin + image.ZDirection * (ctSliceIndex * image.ZRes);

            // Projisoidaan piste annosmatriisin Z-akselille
            VVector relativeToDoseOrigin = ctPlaneCenterWorld - dose.Origin;
            int doseSlice = (int)Math.Round(relativeToDoseOrigin.Dot(dose.ZDirection) / dose.ZRes);

            // Tarkistetaan, osuuko nykyinen CT-leike annosmatriisin alueelle
            if (doseSlice < 0 || doseSlice >= dose.ZSize)
            {
                SliceInfo.Text = $"CT Z: {ctSliceIndex} | Dose Z: {doseSlice} (Annosmatriisin ulkopuolella)";
                return;
            }

            // --------------------------------------------------------------------------------
            // 4. Piirtologiikka (Color Wash)
            // --------------------------------------------------------------------------------
            // Määritellään väritasot suhteessa referenssiannokseen (100%).
            // Piirtojärjestys: Pienimmästä suurimpaan (päällekkäin piirrettäessä).
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

            // Lasketaan skaalauskerroin renderöintiä varten (Dose Pixel -> CT Pixel)
            double scaleX = dose.XRes / image.XRes;
            double scaleY = dose.YRes / image.YRes;
            double maxDoseInSlice = 0;

            // Käydään läpi annosmatriisin pikselit
            for (int y = 0; y < dy; y++)
            {
                for (int x = 0; x < dx; x++)
                {
                    // A. Muunnetaan raaka int-arvo ESAPI-yksiköksi (esim. %)
                    double valInUnits = doseBuffer[x, y] * rawScale + rawOffset;

                    // B. Muunnetaan ESAPI-yksikkö fysikaaliseksi Gray-arvoksi
                    double dGy = valInUnits * unitToGyFactor;

                    if (dGy > maxDoseInSlice) maxDoseInSlice = dGy;

                    // C. Määritetään pikselin väri korkeimman ylittyneen kynnysarvon perusteella
                    Color? pixelColor = null;
                    for (int i = levels.Length - 1; i >= 0; i--)
                    {
                        if (dGy >= (referenceDoseGy * levels[i].Pct))
                        {
                            pixelColor = levels[i].Color;
                            break;
                        }
                    }

                    // D. Piirretään suorakulmio, jos kynnysarvo ylittyy
                    if (pixelColor.HasValue)
                    {
                        // Lasketaan pikselin maailmankoordinaatit
                        VVector worldPos = dose.Origin +
                                           dose.XDirection * (x * dose.XRes) +
                                           dose.YDirection * (y * dose.YRes) +
                                           dose.ZDirection * (doseSlice * dose.ZRes);

                        // Muunnetaan maailmankoordinaatit CT-kuvan pikselikoordinaateiksi
                        VVector diff = worldPos - image.Origin;
                        double px = diff.Dot(image.XDirection) / image.XRes;
                        double py = diff.Dot(image.YDirection) / image.YRes;

                        // Bounds check: piirretään vain kankaan sisäpuolelle
                        if (px >= -scaleX && px < _width && py >= -scaleY && py < _height)
                        {
                            // Luodaan Rectangle-objekti edustamaan annospikseliä
                            Rectangle rect = new Rectangle
                            {
                                Width = scaleX,
                                Height = scaleY,
                                Fill = new SolidColorBrush(pixelColor.Value) { Opacity = 0.3 }, // Läpinäkyvyys 30%
                                IsHitTestVisible = false // Optimointi: poistetaan hiiritapahtumat
                            };

                            // Asetetaan sijainti (keskitettynä laskettuun pisteeseen)
                            Canvas.SetLeft(rect, px - (scaleX / 2.0));
                            Canvas.SetTop(rect, py - (scaleY / 2.0));

                            IsodoseCanvas.Children.Add(rect);
                        }
                    }
                }
            }

            // Päivitetään info-teksti debuggausta ja käyttäjää varten
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
                    int currentCtSlice = (int)SliceSlider.Value;
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