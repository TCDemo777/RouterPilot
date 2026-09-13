using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using RouterPilot.ViewModels;
namespace RouterPilot.Views;
public partial class SqmManagementTabView : UserControl { public SqmManagementTabView() { InitializeComponent(); DataContext = ((App)Application.Current).Services.GetRequiredService<SqmManagementViewModel>(); } }
