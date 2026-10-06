namespace TillPOS.App.Views;

public partial class LoginView : System.Windows.Controls.UserControl
{
    public LoginView()
    {
        InitializeComponent();
        Loaded += (_, _) => Focus();
        PreviewTextInput += (_, e) =>
        {
            if (DataContext is not TillPOS.Presentation.LoginViewModel vm) return;
            foreach (var c in e.Text)
                if (c is >= '0' and <= '9') vm.DigitCommand.Execute(c.ToString());
            e.Handled = true;
        };
    }
}
