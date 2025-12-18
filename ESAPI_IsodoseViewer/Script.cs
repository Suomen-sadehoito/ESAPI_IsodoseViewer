using System.Windows;
using VMS.TPS.Common.Model.API;
using System.Reflection;

[assembly: ESAPIScript(IsWriteable = false)]

namespace VMS.TPS
{
    public class Script
    {
        public void Execute(ScriptContext context)
        {
            if (context.Patient == null || context.Image == null)
            {
                MessageBox.Show("Avaa potilas ja kuva (Image) ennen skriptin ajoa.");
                return;
            }

            // Avataan viewer
            var window = new ESAPI_IsodoseViewer.ViewerWindow(context);
            window.ShowDialog();
        }
    }
}