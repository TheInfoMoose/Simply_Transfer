using System;
using System.Windows;

namespace SimplyTransfer.UI
{
    public partial class SetupProgressWindow : Window
    {
        public SetupProgressWindow()
        {
            InitializeComponent();
        }

        public void AppendLog(string message)
        {
            Dispatcher.Invoke(() =>
            {
                LogTextBlock.Text += message + Environment.NewLine;
                LogScrollViewer.ScrollToEnd();
            });
        }
        
        public void Complete()
        {
            Dispatcher.Invoke(() =>
            {
                ActivityProgressBar.IsIndeterminate = false;
                ActivityProgressBar.Value = 100;
                AppendLog("Configuration complete. Finishing up...");
            });
        }
    }
}
