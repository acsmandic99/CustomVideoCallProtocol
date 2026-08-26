using System.IO;
using System.Windows;

namespace VideoCall.Client.Wpf
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            DispatcherUnhandledException += (s, ex) =>
            {
                File.WriteAllText("error.log", ex.Exception.ToString());
                MessageBox.Show(
                    ex.Exception.GetType().Name + ": " + ex.Exception.Message + "\n\nFull details in error.log next to the exe.",
                    "Unexpected error");
                ex.Handled = true;
            };
        }
    }
}
