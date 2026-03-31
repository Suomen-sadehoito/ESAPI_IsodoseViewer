using EQD2Viewer.Core.Calculations;
using ESAPI_EQD2Viewer.Core.Models;
using ESAPI_EQD2Viewer.Services;
using OxyPlot;
using OxyPlot.Series;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using VMS.TPS.Common.Model.API;
using VMS.TPS.Common.Model.Types;
using EQD2Viewer.Core.Models;
using EQD2Viewer.Core.Data;

namespace ESAPI_EQD2Viewer.UI.ViewModels
{
    public partial class MainViewModel
    {
        public void AddStructuresForDVH(IEnumerable<Structure> structures)
        {
            if (_plan == null || structures == null) return;

            foreach (var structure in structures)
            {
                if (_dvhCache.Any(c => c.Structure.Id == structure.Id)) continue;

                DVHData dvhData = _dvhService.GetDVH(_plan, structure);
                if (dvhData == null) continue;

                _dvhCache.Add(new DVHCacheEntry { Plan = _plan, Structure = structure, DVHData = dvhData });

                if (!_visibleStructures.Any(s => s.Id == structure.Id))
                    _visibleStructures.Add(structure);

                double defaultAB = (structure.DicomType == "PTV" || structure.DicomType == "CTV" || structure.DicomType == "GTV")
                    ? 10.0 : 3.0;

                var settingItem = new StructureAlphaBetaItem(structure, defaultAB);
                settingItem.PropertyChanged += OnStructureSettingChanged;
                StructureSettings.Add(settingItem);

                SummaryData.Add(_dvhService.BuildPhysicalSummary(_plan, structure, dvhData));

                var color = OxyColor.FromArgb(structure.Color.A, structure.Color.R, structure.Color.G, structure.Color.B);
                var series = new LineSeries
                {
                    Title = $"{structure.Id} ({_plan.Id})",
                    Tag = $"Physical_{_plan.Id}_{structure.Id}",
                    Color = color, StrokeThickness = 2
                };
                series.Points.AddRange(dvhData.CurveData.Select(p => new DataPoint(ConvertDoseToGy(p.DoseValue), p.Volume)));
                PlotModel.Series.Add(series);
            }

            if (_isEQD2Enabled) RecalculateAllDVH();
            ShowStructureContours = true;
            RefreshPlot();
            RequestRender();
        }

        public void ClearDVH()
        {
            _dvhCache.Clear();
            _visibleStructures.Clear();
            StructureSettings.Clear();
            PlotModel.Series.Clear();
            SummaryData.Clear();
            RefreshPlot();
            RequestRender();
        }

        internal void RecalculateAllDVH()
        {
            var oldSeries = PlotModel.Series.Where(s => (s.Tag as string)?.StartsWith("EQD2_") ?? false).ToList();
            foreach (var s in oldSeries) PlotModel.Series.Remove(s);
            var oldSummaries = SummaryData.Where(s => s.Type == "EQD2").ToList();
            foreach (var s in oldSummaries) SummaryData.Remove(s);

            if (!_isEQD2Enabled) { RefreshPlot(); return; }

            foreach (var entry in _dvhCache)
            {
                var setting = StructureSettings.FirstOrDefault(s => s.Structure.Id == entry.Structure.Id);
                double alphaBeta = setting?.AlphaBeta ?? 3.0;

                SummaryData.Add(_dvhService.BuildEQD2Summary(entry.Plan, entry.Structure, entry.DVHData,
                    _numberOfFractions, alphaBeta, _meanMethod));

                var curveInGy = entry.DVHData.CurveData.Select(p =>
                    new DoseVolumePoint(ConvertDoseToGy(p.DoseValue), p.Volume)).ToArray();

                var eqd2Curve = EQD2Calculator.ConvertCurveToEQD2(curveInGy, _numberOfFractions, alphaBeta);
                var color = OxyColor.FromArgb(entry.Structure.Color.A, entry.Structure.Color.R,
                    entry.Structure.Color.G, entry.Structure.Color.B);

                var eqd2Series = new LineSeries
                {
                    Title = $"{entry.Structure.Id} EQD2 (α/β={alphaBeta:F1})",
                    LineStyle = LineStyle.Dash,
                    Tag = $"EQD2_{entry.Plan.Id}_{entry.Structure.Id}",
                    Color = color, StrokeThickness = 2
                };
                eqd2Series.Points.AddRange(eqd2Curve.Select(p => new DataPoint(p.DoseGy, p.VolumePercent)));
                PlotModel.Series.Add(eqd2Series);
            }
            RefreshPlot();
        }

        private void OnStructureSettingChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(StructureAlphaBetaItem.AlphaBeta) && _isEQD2Enabled)
                RecalculateAllDVH();
        }

        internal void UpdatePlotVisibility()
        {
            foreach (var series in PlotModel.Series)
                if (series.Tag is string tag)
                    series.IsVisible = (tag.StartsWith("Physical_") && _showPhysicalDVH) ||
                                       (tag.StartsWith("EQD2_") && _showEQD2DVH) ||
                                       tag.StartsWith("Summation_");
            PlotModel.InvalidatePlot(true);
        }

        internal void RefreshPlot() { UpdatePlotVisibility(); PlotModel.InvalidatePlot(true); }

        private static double ConvertDoseToGy(DoseValue dv) =>
            dv.Unit == DoseValue.DoseUnit.cGy ? dv.Dose / 100.0 : dv.Dose;
    }
}
