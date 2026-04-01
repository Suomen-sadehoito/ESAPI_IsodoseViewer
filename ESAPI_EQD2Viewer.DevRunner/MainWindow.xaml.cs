using System.Windows;

namespace ESAPI_EQD2Viewer.DevRunner
{
    /// <summary>
    /// Stub launcher window — App.xaml.cs handles all startup logic and opens the real MainWindow.
    /// This window closes itself immediately after construction.
    /// </summary>
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            // App.xaml.cs already created the real MainWindow; close this stub.
            Close();
        }
    }
}