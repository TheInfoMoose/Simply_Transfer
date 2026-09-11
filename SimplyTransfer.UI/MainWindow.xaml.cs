using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using SimplyTransfer.UI.ViewModels;

namespace SimplyTransfer.UI
{
    /// <summary>
    /// Code-behind for the primary application window, resolving DataContext from dependency injection.
    /// </summary>
    public partial class MainWindow : Window
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="MainWindow"/> class.
        /// </summary>
        public MainWindow()
        {
            InitializeComponent();
            DataContext = App.ServiceProvider.GetRequiredService<MainViewModel>();
        }
    }
}
