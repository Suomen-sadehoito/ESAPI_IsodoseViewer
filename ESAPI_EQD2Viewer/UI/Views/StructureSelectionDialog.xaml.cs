using System.Collections.Generic;
using System.Linq;
using System.Windows;
using ESAPI_EQD2Viewer.Core.Data;

namespace ESAPI_EQD2Viewer.UI.Views
{
    public partial class StructureSelectionDialog : Window
    {
        public IEnumerable<StructureData> SelectedStructures { get; private set; }

        public StructureSelectionDialog(IEnumerable<StructureData> structures)
        {
            InitializeComponent();
            if (structures != null)
            {
                StructureListBox.ItemsSource = structures
                    .Where(s => !s.IsEmpty)
                    .GroupBy(s => s.Id)
                    .Select(g => g.First())
                    .OrderBy(s => s.Id)
                    .ToList();
            }
        }

        private void OK_Click(object sender, RoutedEventArgs e)
        {
            SelectedStructures = StructureListBox.SelectedItems.Cast<StructureData>().ToList();
            DialogResult = true;
        }
    }
}
