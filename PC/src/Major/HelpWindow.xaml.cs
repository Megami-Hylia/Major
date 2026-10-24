using System.Windows;
namespace Major;
public partial class HelpWindow : Window
{
    public HelpWindow() => InitializeComponent();
    private void CloseClick(object sender, RoutedEventArgs e) => Close();
}
