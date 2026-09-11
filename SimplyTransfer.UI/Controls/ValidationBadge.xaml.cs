using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SimplyTransfer.Core.Models;

namespace SimplyTransfer.UI.Controls
{
    /// <summary>
    /// Custom WPF user control that renders a styled cryptographic hash validation badge
    /// reflecting Pending, Validating, Verified, or Failed integrity status.
    /// </summary>
    public partial class ValidationBadge : UserControl
    {
        /// <summary>Identifies the <see cref="IsValid"/> dependency property.</summary>
        public static readonly DependencyProperty IsValidProperty =
            DependencyProperty.Register(
                nameof(IsValid), 
                typeof(bool?), 
                typeof(ValidationBadge), 
                new PropertyMetadata(null, OnIsValidChanged));

        /// <summary>Identifies the <see cref="Status"/> dependency property.</summary>
        public static readonly DependencyProperty StatusProperty =
            DependencyProperty.Register(
                nameof(Status), 
                typeof(HashMatchStatus), 
                typeof(ValidationBadge), 
                new PropertyMetadata(HashMatchStatus.Pending, OnStatusChanged));

        /// <summary>
        /// Gets or sets a nullable boolean flag indicating verification state (true: verified, false: mismatch, null: pending).
        /// </summary>
        public bool? IsValid
        {
            get => (bool?)GetValue(IsValidProperty);
            set => SetValue(IsValidProperty, value);
        }

        /// <summary>
        /// Gets or sets the explicit <see cref="HashMatchStatus"/> for the badge.
        /// </summary>
        public HashMatchStatus Status
        {
            get => (HashMatchStatus)GetValue(StatusProperty);
            set => SetValue(StatusProperty, value);
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="ValidationBadge"/> class.
        /// </summary>
        public ValidationBadge()
        {
            InitializeComponent();
            UpdateVisualState();
        }

        private static void OnIsValidChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is ValidationBadge badge)
            {
                var value = (bool?)e.NewValue;
                if (value == true)
                    badge.Status = HashMatchStatus.Verified;
                else if (value == false)
                    badge.Status = HashMatchStatus.Mismatch;
                else
                    badge.Status = HashMatchStatus.Pending;
            }
        }

        private static void OnStatusChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is ValidationBadge badge)
            {
                badge.UpdateVisualState();
            }
        }

        private void UpdateVisualState()
        {
            // Reset all icons
            DotPending.Visibility = Visibility.Collapsed;
            IconVerified.Visibility = Visibility.Collapsed;
            IconFailed.Visibility = Visibility.Collapsed;
            IconValidating.Visibility = Visibility.Collapsed;

            switch (Status)
            {
                case HashMatchStatus.Verified:
                    BadgeBorder.Background = new SolidColorBrush(Color.FromArgb(50, 16, 185, 129)); // Soft green
                    BadgeBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(16, 185, 129));
                    IconVerified.Visibility = Visibility.Visible;
                    StatusText.Text = "Verified";
                    StatusText.Foreground = new SolidColorBrush(Color.FromRgb(52, 211, 153));
                    break;

                case HashMatchStatus.Mismatch:
                    BadgeBorder.Background = new SolidColorBrush(Color.FromArgb(50, 239, 68, 68)); // Soft red
                    BadgeBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(239, 68, 68));
                    IconFailed.Visibility = Visibility.Visible;
                    StatusText.Text = "Failed";
                    StatusText.Foreground = new SolidColorBrush(Color.FromRgb(248, 113, 113));
                    break;

                case HashMatchStatus.Validating:
                    BadgeBorder.Background = new SolidColorBrush(Color.FromArgb(50, 245, 158, 11)); // Soft amber
                    BadgeBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(245, 158, 11));
                    IconValidating.Visibility = Visibility.Visible;
                    StatusText.Text = "Validating...";
                    StatusText.Foreground = new SolidColorBrush(Color.FromRgb(251, 191, 36));
                    break;

                case HashMatchStatus.Pending:
                default:
                    BadgeBorder.Background = new SolidColorBrush(Color.FromRgb(37, 45, 56)); // Neutral slate
                    BadgeBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(59, 72, 90));
                    DotPending.Visibility = Visibility.Visible;
                    StatusText.Text = "Pending";
                    StatusText.Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184));
                    break;
            }
        }
    }
}
