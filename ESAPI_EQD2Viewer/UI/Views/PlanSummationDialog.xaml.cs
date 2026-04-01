using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using ESAPI_EQD2Viewer.Core.Data;
using ESAPI_EQD2Viewer.Core.Models;

namespace ESAPI_EQD2Viewer.UI.Views
{
    public partial class PlanSummationDialog : Window
    {
        private readonly List<CourseData> _courses;
        private readonly List<RegistrationData> _registrations;
        private readonly PlanData _currentPlan;
        private List<RegistrationInfo> _allRegistrations;
        public ObservableCollection<PlanRowItem> PlanRows { get; } = new ObservableCollection<PlanRowItem>();
        public SummationConfig ResultConfig { get; private set; }

        public PlanSummationDialog(List<CourseData> courses, List<RegistrationData> registrations, PlanData currentPlan)
        {
            InitializeComponent();
            _courses = courses ?? new List<CourseData>();
            _registrations = registrations ?? new List<RegistrationData>();
            _currentPlan = currentPlan;
            _allRegistrations = IndexAllRegistrations();
            PopulatePlans();
            PlanGrid.ItemsSource = PlanRows;
        }

        private List<RegistrationInfo> IndexAllRegistrations()
        {
            var list = new List<RegistrationInfo>();
            foreach (var reg in _registrations)
            {
                try
                {
                    list.Add(new RegistrationInfo
                    {
                        Id = reg.Id,
                        SourceFOR = reg.SourceFOR ?? "",
                        RegisteredFOR = reg.RegisteredFOR ?? "",
                        DateStr = reg.CreationDateTime?.ToString("d") ?? ""
                    });
                }
                catch { }
            }
            return list;
        }

        private List<RegistrationOption> FilterRegistrationsForPair(string planFOR, string referenceFOR)
        {
            var options = new List<RegistrationOption>
            {
                new RegistrationOption { Id = "", DisplayName = "None — same CT as reference" }
            };
            if (string.IsNullOrEmpty(planFOR) || string.IsNullOrEmpty(referenceFOR)) return options;
            if (string.Equals(planFOR, referenceFOR, StringComparison.OrdinalIgnoreCase)) return options;

            foreach (var reg in _allRegistrations)
            {
                bool planIsSource = string.Equals(reg.SourceFOR, planFOR, StringComparison.OrdinalIgnoreCase);
                bool planIsTarget = string.Equals(reg.RegisteredFOR, planFOR, StringComparison.OrdinalIgnoreCase);
                bool refIsSource = string.Equals(reg.SourceFOR, referenceFOR, StringComparison.OrdinalIgnoreCase);
                bool refIsTarget = string.Equals(reg.RegisteredFOR, referenceFOR, StringComparison.OrdinalIgnoreCase);

                if (planIsSource && refIsTarget)
                    options.Add(new RegistrationOption { Id = reg.Id, DisplayName = $"{reg.Id}  [plan \u2192 ref]  ({reg.DateStr})" });
                else if (refIsSource && planIsTarget)
                    options.Add(new RegistrationOption { Id = reg.Id, DisplayName = $"{reg.Id}  [ref \u2192 plan]  ({reg.DateStr})" });
            }
            return options;
        }

        private string _lastDebugReport = "";

        private void RebuildAllRegistrationLists()
        {
            var refRow = PlanRows.FirstOrDefault(r => r.IsReference);
            string referenceFOR = refRow?.ImageFOR ?? "";

            var dbg = new System.Text.StringBuilder();
            dbg.AppendLine("EQD2 VIEWER — REGISTRATION DEBUG REPORT");
            dbg.AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            dbg.AppendLine($"Reference FOR: {referenceFOR}");
            dbg.AppendLine($"Total registrations: {_allRegistrations.Count}");
            dbg.AppendLine();

            foreach (var row in PlanRows)
            {
                if (row.IsReference)
                {
                    row.RelevantRegistrations = new List<RegistrationOption>();
                    row.SelectedRegistrationId = "";
                    dbg.AppendLine($"{row.CourseId}/{row.PlanId}: REFERENCE");
                    continue;
                }

                var newList = FilterRegistrationsForPair(row.ImageFOR, referenceFOR);
                string currentSelection = row.SelectedRegistrationId;
                bool selectionStillValid = newList.Any(o => o.Id == currentSelection);
                row.RelevantRegistrations = newList;
                if (!selectionStillValid) row.SelectedRegistrationId = "";

                int matchCount = newList.Count - 1;
                dbg.AppendLine($"{row.CourseId}/{row.PlanId}: FOR={row.ImageFOR}, {matchCount} registrations");
            }

            _lastDebugReport = dbg.ToString();
            if (TbDebugInfo != null) TbDebugInfo.Text = _lastDebugReport;
        }

        private void CopyDebug_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(_lastDebugReport))
            {
                try { Clipboard.SetText(_lastDebugReport); MessageBox.Show("Copied to clipboard."); }
                catch (Exception ex) { MessageBox.Show($"Error: {ex.Message}"); }
            }
        }

        private void PopulatePlans()
        {
            foreach (var course in _courses)
            {
                if (course.Plans == null) continue;
                foreach (var plan in course.Plans)
                {
                    if (!plan.HasDose) continue;

                    bool isCurrentPlan = _currentPlan != null
                        && plan.PlanId == _currentPlan.Id
                        && plan.CourseId == _currentPlan.CourseId;

                    var row = new PlanRowItem
                    {
                        CourseId = course.Id, PlanId = plan.PlanId,
                        ImageId = plan.ImageId ?? "", ImageFOR = plan.ImageFOR ?? "",
                        TotalDoseGy = plan.TotalDoseGy, NumberOfFractions = plan.NumberOfFractions,
                        PlanNormalization = plan.PlanNormalization,
                        IsIncluded = isCurrentPlan, IsReference = isCurrentPlan,
                        SelectedRegistrationId = "", Weight = 1.0,
                        RelevantRegistrations = new List<RegistrationOption>()
                    };
                    row.PropertyChanged += OnRowPropertyChanged;
                    PlanRows.Add(row);
                }
            }
            RebuildAllRegistrationLists();
        }

        private void OnRowPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(PlanRowItem.IsReference)) RebuildAllRegistrationLists();
        }

        private void Compute_Click(object sender, RoutedEventArgs e)
        {
            var includedPlans = PlanRows.Where(p => p.IsIncluded).ToList();
            if (includedPlans.Count < 2) { MessageBox.Show("Select at least two plans."); return; }
            if (!includedPlans.Any(p => p.IsReference)) { MessageBox.Show("Mark one plan as reference."); return; }
            if (includedPlans.Count(p => p.IsReference) > 1) { MessageBox.Show("Only one reference plan allowed."); return; }

            foreach (var p in includedPlans)
                if (p.NumberOfFractions <= 0) { MessageBox.Show($"{p.CourseId}/{p.PlanId}: fractions must be >= 1."); return; }

            var refPlan = includedPlans.First(p => p.IsReference);
            foreach (var p in includedPlans.Where(p => !p.IsReference))
            {
                bool sameFOR = !string.IsNullOrEmpty(p.ImageFOR) && !string.IsNullOrEmpty(refPlan.ImageFOR)
                    && string.Equals(p.ImageFOR, refPlan.ImageFOR, StringComparison.OrdinalIgnoreCase);
                if (!sameFOR && string.IsNullOrEmpty(p.SelectedRegistrationId))
                {
                    if (MessageBox.Show($"Plan \"{p.CourseId}/{p.PlanId}\" has no registration. Continue?",
                        "Warning", MessageBoxButton.YesNo) == MessageBoxResult.No) return;
                }
            }

            double alphaBeta;
            if (!double.TryParse(TbAlphaBeta.Text, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out alphaBeta) || alphaBeta <= 0)
                alphaBeta = 3.0;

            ResultConfig = new SummationConfig
            {
                Method = RbEQD2.IsChecked == true ? SummationMethod.EQD2 : SummationMethod.Physical,
                GlobalAlphaBeta = alphaBeta,
                Plans = includedPlans.Select(p => new SummationPlanEntry
                {
                    DisplayLabel = $"{p.CourseId} / {p.PlanId}", CourseId = p.CourseId, PlanId = p.PlanId,
                    NumberOfFractions = p.NumberOfFractions, TotalDoseGy = p.TotalDoseGy,
                    PlanNormalization = double.IsNaN(p.PlanNormalization) || p.PlanNormalization <= 0 ? 100.0 : p.PlanNormalization,
                    IsReference = p.IsReference,
                    RegistrationId = p.IsReference ? null : p.SelectedRegistrationId,
                    Weight = p.Weight
                }).ToList()
            };
            DialogResult = true;
        }
    }

    public class PlanRowItem : INotifyPropertyChanged
    {
        private bool _isIncluded, _isReference;
        private int _numberOfFractions;
        private double _weight = 1.0;
        private string _selectedRegistrationId = "";
        private List<RegistrationOption> _relevantRegistrations = new List<RegistrationOption>();

        public string CourseId { get; set; }
        public string PlanId { get; set; }
        public string ImageId { get; set; }
        public string ImageFOR { get; set; }
        public double TotalDoseGy { get; set; }
        public double PlanNormalization { get; set; }

        public string ShortFOR => string.IsNullOrEmpty(ImageFOR) ? "\u2014"
            : (ImageFOR.Length > 8 ? ".." + ImageFOR.Substring(ImageFOR.Length - 8) : ImageFOR);

        public List<RegistrationOption> RelevantRegistrations
        { get => _relevantRegistrations; set { _relevantRegistrations = value; OnPropertyChanged(); } }
        public bool IsIncluded { get => _isIncluded; set { _isIncluded = value; OnPropertyChanged(); } }
        public bool IsReference
        {
            get => _isReference;
            set { if (_isReference != value) { _isReference = value; OnPropertyChanged(); if (value) SelectedRegistrationId = ""; } }
        }
        public int NumberOfFractions { get => _numberOfFractions; set { _numberOfFractions = value; OnPropertyChanged(); } }
        public double Weight { get => _weight; set { _weight = value; OnPropertyChanged(); } }
        public string SelectedRegistrationId { get => _selectedRegistrationId; set { _selectedRegistrationId = value; OnPropertyChanged(); } }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public class RegistrationOption { public string Id { get; set; } public string DisplayName { get; set; } }
    internal class RegistrationInfo { public string Id { get; set; } public string SourceFOR { get; set; } public string RegisteredFOR { get; set; } public string DateStr { get; set; } }
}
