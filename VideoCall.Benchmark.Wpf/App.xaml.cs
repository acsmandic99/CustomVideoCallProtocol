using System.IO;
using System.Windows;

namespace VideoCall.Benchmark.Wpf
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            DispatcherUnhandledException += (s, ex) =>
            {
                File.WriteAllText("benchmark-error.log", ex.Exception.ToString());
                MessageBox.Show(
                    ex.Exception.GetType().Name + ": " + ex.Exception.Message + "\n\nFull details in benchmark-error.log next to the exe.",
                    "Unexpected error");
                ex.Handled = true;
            };
        }
    }
}
