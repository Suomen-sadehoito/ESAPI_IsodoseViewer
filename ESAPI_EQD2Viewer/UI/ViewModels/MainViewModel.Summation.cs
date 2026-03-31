using CommunityToolkit.Mvvm.Input;
using EQD2Viewer.Core.Calculations;
using EQD2Viewer.Core.Data;
using EQD2Viewer.Core.Interfaces;
using EQD2Viewer.Core.Logging;
using EQD2Viewer.Core.Models;
using ESAPI_EQD2Viewer.Core.Interfaces;
using ESAPI_EQD2Viewer.Core.Models;
using ESAPI_EQD2Viewer.Services;
using ESAPI_EQD2Viewer.UI.Views;
using OxyPlot;
using OxyPlot.Series;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace ESAPI_EQD2Viewer.UI.ViewModels
{
    public partial class MainViewModel
    {
        private ISummationService _summationService;
        private SummationConfig _activeSummationConfig;
        private CancellationTokenSource _summationCts;
        private CancellationTokenSource _recomputeCts;
        private DispatcherTimer _displayAlphaBetaDebounce;

        private bool _isSummationActive;
        public bool IsSummationActive
        {
            get => _isSummationActive;
            set { if (SetProperty(ref _isSummationActive, value)) { OnPropertyChanged(nameof(SummationStatusLabel)); RequestRender(); } }
        }

        private bool _isSummationComputing;
        public bool IsSummationComputing { get => _isSummationComputing; set => SetProperty(ref _isSummationComputing, value); }

        private int _summationProgress;
        public int SummationProgress { get => _summationProgress; set => SetProperty(ref _summationProgress, value); }

        private string _summationInfo = "No summation active";
        public string SummationInfo { get => _summationInfo; set => SetProperty(ref _summationInfo, value); }

        public string SummationStatusLabel => _isSummationActive ? "Summation active" : "";

        [RelayCommand]
        private async Task OpenSummationDialog()
        {
            if (_summationDataLoader == null)
            {
                MessageBox.Show("Plan summation is not available in this mode.\n" +
                                "Summation requires full clinical data access.",
                                "EQD2 Viewer", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dialog = new PlanSummationDialog(
                _snapshot.AllCourses,
                _snapshot.Registrations,
                _snapshot.ActivePlan);
            dialog.Owner = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive);
            if (dialog.ShowDialog() == true && dialog.ResultConfig != null)
                await ExecuteSummationAsync(dialog.ResultConfig);
        }

        [RelayCommand]
        private void CancelSummation()
        {
            _summationCts?.Cancel();
            _recomputeCts?.Cancel();
        }

        [RelayCommand]
        private void ClearSummation()
        {
            _summationCts?.Cancel();
            _recomputeCts?.Cancel();
            _summationService?.Dispose();
            _summationService = null;
            _activeSummationConfig = null;
            IsSummationActive = false;
            IsSummationComputing = false;
            SummationProgress = 0;
            SummationInfo = "No summation active";
            SummationAlphaBetaLabel = "";
            CurrentOverlayMode = OverlayMode.Off;
            OverlayPlanOptions.Clear();
            ClearSummationDVH();
            if (_isodoseMode == IsodoseMode.Absolute) LoadIsodosePreset("Eclipse");
            RequestRender();
        }

        private async Task ExecuteSummationAsync(SummationConfig config)
        {
            _summationCts?.Cancel();
            _recomputeCts?.Cancel();
            _summationCts = new CancellationTokenSource();
            var ct = _summationCts.Token;
            IsSummationComputing = true;
            SummationProgress = 0;
            SummationInfo = "Loading plan data...";

            try
            {
                _summationService?.Dispose();
                _summationService = new SummationService(_snapshot.CtImage, _summationDataLoader, _snapshot.Registrations);
                var prepResult = _summationService.PrepareData(config);
                if (!prepResult.Success)
                {
                    MessageBox.Show($"Failed:\n{prepResult.StatusMessage}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    IsSummationComputing = false; return;
                }

                StatusText = prepResult.StatusMessage;
                SummationInfo = "Computing...";
                var progress = new Progress<int>(pct => { SummationProgress = pct; SummationInfo = $"Computing... {pct}%"; });
                ct.ThrowIfCancellationRequested();
                var result = await _summationService.ComputeAsync(progress, ct);

                if (result.Success)
                {
                    _activeSummationConfig = config;
                    IsSummationActive = true;

                    string ml = config.Method == SummationMethod.EQD2 ? "EQD2" : "Physical";
                    SummationInfo = $"{ml} sum: {config.Plans.Count} plans | Max: {result.MaxDoseGy:F2} Gy | Ref: {result.TotalReferenceDoseGy:F2} Gy";
                    StatusText = result.StatusMessage;

                    _displayAlphaBeta = config.GlobalAlphaBeta;
                    OnPropertyChanged(nameof(DisplayAlphaBeta));
                    SummationAlphaBetaLabel = $"Summation computed with α/β = {config.GlobalAlphaBeta:F1} Gy";

                    if (_isodoseMode != IsodoseMode.Absolute) LoadIsodosePreset("ReIrradiation");

                    OverlayPlanOptions.Clear();
                    foreach (var plan in config.Plans.Where(p => !p.IsReference))
                        OverlayPlanOptions.Add(plan.DisplayLabel);
                    if (OverlayPlanOptions.Count > 0) SelectedOverlayPlanLabel = OverlayPlanOptions[0];

                    CalculateSummationDVH(result.MaxDoseGy);
                    RequestRender();
                }
                else
                {
                    SummationInfo = result.StatusMessage;
                    if (!ct.IsCancellationRequested)
                        MessageBox.Show($"Failed:\n{result.StatusMessage}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (OperationCanceledException) { SummationInfo = "Cancelled."; }
            catch (Exception ex) { SimpleLogger.Error("Summation failed", ex); MessageBox.Show($"Error:\n{ex.Message}"); }
            finally { IsSummationComputing = false; }
        }

        internal void RecomputeDisplayEQD2IfActive()
        {
            if (!_isSummationActive || _summationService == null) return;

            if (_displayAlphaBetaDebounce == null)
            {
                _displayAlphaBetaDebounce = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(RenderConstants.AlphaBetaDebounceMs)
                };
                _displayAlphaBetaDebounce.Tick += async (s, e) =>
                {
                    _displayAlphaBetaDebounce.Stop();
                    await RecomputeDisplayEQD2Core();
                };
            }
            _displayAlphaBetaDebounce.Stop();
            _displayAlphaBetaDebounce.Start();
        }

        private async Task RecomputeDisplayEQD2Core()
        {
            if (_summationService == null || !_isSummationActive) return;

            _recomputeCts?.Cancel();
            _recomputeCts = new CancellationTokenSource();
            var ct = _recomputeCts.Token;

            try
            {
                IsSummationComputing = true;
                SummationInfo = $"Updating display α/β = {_displayAlphaBeta:F1} Gy...";

                var progress = new Progress<int>(pct => SummationProgress = pct);
                var result = await _summationService.RecomputeEQD2DisplayAsync(
                    _displayAlphaBeta, progress, ct);

                if (result.Success)
                {
                    SummationInfo = result.StatusMessage;
                    StatusText = result.StatusMessage;
                    RequestRender();
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                SimpleLogger.Error("RecomputeDisplayEQD2 failed", ex);
            }
            finally { IsSummationComputing = false; }
        }

        private void CalculateSummationDVH(double maxDoseGy)
        {
            if (_summationService == null || !_summationService.HasSummedDose) return;
            var structureIds = _summationService.GetCachedStructureIds();
            if (structureIds == null || structureIds.Count == 0) return;

            var selectedIds = _dvhCache.Select(c => c.Structure.Id).ToHashSet();
            double voxelVolCc = _summationService.GetVoxelVolumeCc();
            int sliceCount = _summationService.SliceCount;
            bool isEqd2Sum = _activeSummationConfig?.Method == SummationMethod.EQD2;

            ClearSummationDVH();

            foreach (var structureId in structureIds)
            {
                if (!selectedIds.Contains(structureId)) continue;

                var structureSetting = StructureSettings.FirstOrDefault(s => s.Id == structureId);
                double structureAlphaBeta = structureSetting?.AlphaBeta ?? 3.0;
                string methodLabel = isEqd2Sum ? $"EQD2 α/β={structureAlphaBeta:F1}" : "Physical Sum";

                DoseVolumePoint[] dvhPoints;

                if (isEqd2Sum)
                {
                    dvhPoints = _summationService.ComputeStructureEQD2DVH(
                        structureId, structureAlphaBeta, maxDoseGy);
                }
                else
                {
                    double[][] summedSlices = new double[sliceCount][];
                    bool[][] masks = new bool[sliceCount][];
                    for (int z = 0; z < sliceCount; z++)
                    {
                        summedSlices[z] = _summationService.GetSummedSlice(z);
                        masks[z] = _summationService.GetStructureMask(structureId, z);
                    }
                    dvhPoints = _dvhService.CalculateDVHFromSummedDose(summedSlices, masks, voxelVolCc, maxDoseGy);
                }

                if (dvhPoints == null || dvhPoints.Length == 0) continue;

                long totalVoxels = 0;
                for (int z = 0; z < sliceCount; z++)
                {
                    bool[] mask = _summationService.GetStructureMask(structureId, z);
                    if (mask != null) for (int i = 0; i < mask.Length; i++) if (mask[i]) totalVoxels++;
                }

                SummaryData.Add(_dvhService.BuildSummaryFromCurve(
                    structureId, "Summation", methodLabel, dvhPoints, totalVoxels * voxelVolCc));

                var cached = _dvhCache.FirstOrDefault(c => c.Structure.Id == structureId);
                OxyColor color = cached != null
                    ? OxyColor.FromArgb(cached.Structure.ColorA, cached.Structure.ColorR, cached.Structure.ColorG, cached.Structure.ColorB)
                    : OxyColors.White;

                var series = new LineSeries
                {
                    Title = $"{structureId} {methodLabel}",
                    Tag = $"Summation_{structureId}",
                    Color = color,
                    StrokeThickness = 2.5,
                    LineStyle = LineStyle.DashDot
                };
                series.Points.AddRange(dvhPoints.Select(p => new DataPoint(p.DoseGy, p.VolumePercent)));
                PlotModel.Series.Add(series);
            }
            RefreshPlot();
        }

        private void ClearSummationDVH()
        {
            foreach (var s in PlotModel.Series.Where(s => (s.Tag as string)?.StartsWith("Summation_") ?? false).ToList())
                PlotModel.Series.Remove(s);
            foreach (var s in SummaryData.Where(s => s.PlanId == "Summation").ToList())
                SummaryData.Remove(s);
        }

        private void RenderSummationScene()
        {
            double[] summedSlice = _summationService.GetSummedSlice(CurrentSlice);
            if (summedSlice == null) return;

            double refDose = _summationService.SummedReferenceDoseGy;
            if (refDose < DomainConstants.MinReferenceDoseGy) refDose = 1.0;

            int w = _snapshot.CtImage.XSize, h = _snapshot.CtImage.YSize;

            if (_doseDisplayMode == DoseDisplayMode.Line)
            {
                ClearDoseBitmap(w, h);
                var contours = new ObservableCollection<IsodoseContourData>();

                foreach (var level in _isodoseLevelArray)
                {
                    if (!level.IsVisible) continue;
                    double thr = GetThresholdGy(level, refDose);
                    if (thr <= 0) continue;
                    var polylines = MarchingSquares.GenerateContours(summedSlice, w, h, thr);
                    if (polylines.Count == 0) continue;

                    var geo = new System.Windows.Media.StreamGeometry();
                    using (var ctx = geo.Open())
                        foreach (var chain in polylines)
                        {
                            if (chain.Count < 2) continue;
                            ctx.BeginFigure(new System.Windows.Point(chain[0].X, chain[0].Y), false, false);
                            for (int j = 1; j < chain.Count; j++) ctx.LineTo(new System.Windows.Point(chain[j].X, chain[j].Y), true, false);
                        }
                    geo.Freeze();

                    uint c = level.Color;
                    var brush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(
                        (byte)((c >> 16) & 0xFF), (byte)((c >> 8) & 0xFF), (byte)(c & 0xFF)));
                    brush.Freeze();
                    contours.Add(new IsodoseContourData { Geometry = geo, Stroke = brush, StrokeThickness = 1.0 });
                }
                ContourLines = contours;
                StatusText = $"[Summation · Line] Slice {CurrentSlice} | Ref: {refDose:F2} Gy | α/β: {_displayAlphaBeta:F1}";
            }
            else
            {
                if (_contourLines?.Count > 0) ContourLines = new ObservableCollection<IsodoseContourData>();
                RenderSummedDoseBitmap(summedSlice, refDose, w, h);
            }
        }

        private unsafe void ClearDoseBitmap(int w, int h)
        {
            DoseImageSource.Lock();
            try
            {
                byte* p = (byte*)DoseImageSource.BackBuffer;
                for (int i = 0; i < h * DoseImageSource.BackBufferStride; i++) p[i] = 0;
                DoseImageSource.AddDirtyRect(new System.Windows.Int32Rect(0, 0, w, h));
            }
            finally { DoseImageSource.Unlock(); }
        }

        private unsafe void RenderSummedDoseBitmap(double[] slice, double refDose, int w, int h)
        {
            DoseImageSource.Lock();
            try
            {
                byte* pBuf = (byte*)DoseImageSource.BackBuffer;
                int stride = DoseImageSource.BackBufferStride;
                for (int i = 0; i < h * stride; i++) pBuf[i] = 0;

                if (_doseDisplayMode == DoseDisplayMode.Fill)
                {
                    int vc = 0;
                    for (int i = 0; i < _isodoseLevelArray.Length; i++) if (_isodoseLevelArray[i].IsVisible) vc++;
                    if (vc > 0)
                    {
                        double[] thr = new double[vc]; uint[] col = new uint[vc]; int vi = 0;
                        for (int i = 0; i < _isodoseLevelArray.Length; i++)
                        {
                            if (!_isodoseLevelArray[i].IsVisible) continue;
                            thr[vi] = GetThresholdGy(_isodoseLevelArray[i], refDose);
                            col[vi] = (_isodoseLevelArray[i].Color & 0x00FFFFFF) | ((uint)_isodoseLevelArray[i].Alpha << 24);
                            vi++;
                        }
                        for (int py = 0; py < h; py++)
                        {
                            uint* row = (uint*)(pBuf + py * stride); int ro = py * w;
                            for (int px = 0; px < w; px++)
                            {
                                double d = slice[ro + px]; if (d <= 0) continue;
                                for (int li = 0; li < vc; li++) if (d >= thr[li]) { row[px] = col[li]; break; }
                            }
                        }
                    }
                }
                else if (_doseDisplayMode == DoseDisplayMode.Colorwash)
                {
                    byte cwA = (byte)(System.Math.Max(0, System.Math.Min(1, _colorwashOpacity)) * 255);
                    double minGy = refDose * _colorwashMinPercent, maxGy = refDose * RenderConstants.ColorwashMaxFraction;
                    double range = maxGy - minGy;
                    if (range > 0)
                        for (int py = 0; py < h; py++)
                        {
                            uint* row = (uint*)(pBuf + py * stride); int ro = py * w;
                            for (int px = 0; px < w; px++)
                            {
                                double d = slice[ro + px]; if (d < minGy) continue;
                                row[px] = ColorMaps.Jet(System.Math.Min(1.0, (d - minGy) / range), cwA);
                            }
                        }
                }

                string ml = _doseDisplayMode == DoseDisplayMode.Fill ? "Fill" : "Colorwash";
                StatusText = $"[Summation · {ml}] Slice {CurrentSlice} | Ref: {refDose:F2} Gy | α/β: {_displayAlphaBeta:F1}";
                DoseImageSource.AddDirtyRect(new System.Windows.Int32Rect(0, 0, w, h));
            }
            finally { DoseImageSource.Unlock(); }
        }
    }
}
