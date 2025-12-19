using System.Windows;
using VMS.TPS.Common.Model.API;
using ESAPI_IsodoseViewer.ViewModels;

namespace ESAPI_IsodoseViewer
{
    public partial class ViewerWindow : Window
    {
        public ViewerWindow(ScriptContext context)
        {
            InitializeComponent();

            // Kytketään ViewModel Viewiin
            DataContext = new MainViewModel(context);
        }
    }
}