namespace TillPOS.App.Views;

public partial class OpenShiftView : System.Windows.Controls.UserControl
{
    public OpenShiftView()
    {
        InitializeComponent();
        // After layout, with a retry: arriving from the login screen, a plain Focus() in Loaded could lose to the login
        // screen's focus clean-up, and typed digits then went nowhere.
        Loaded += (_, _) => Keyboarding.FocusWhenReady(this, () => CashBox);
    }
}
