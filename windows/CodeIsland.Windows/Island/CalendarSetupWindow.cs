using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using static CodeIsland.Windows.Island.IslandStyle;
using Cursors = System.Windows.Input.Cursors;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;
using TextBox = System.Windows.Controls.TextBox;

namespace CodeIsland.Windows.Island;

/// Asks for the Apple ID and an app-specific password, checks them against iCloud,
/// then stores them in Credential Manager. The password never touches a file.
public sealed class CalendarSetupWindow : Window
{
    private readonly CalendarService _calendar;
    private readonly TextBox _appleId;
    private readonly PasswordBox _password;
    private readonly TextBlock _status;
    private readonly Border _connect;
    private bool _busy;

    public CalendarSetupWindow(CalendarService calendar)
    {
        _calendar = calendar;
        Title = "Connect iCloud Calendar";
        Width = 420;
        SizeToContent = SizeToContent.Height;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = System.Windows.Media.Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;
        ShowInTaskbar = true;

        var stack = new StackPanel { Margin = new Thickness(22, 18, 22, 20) };
        var title = Text("Connect iCloud Calendar", 16, Primary, Ui, FontWeights.SemiBold);
        stack.Children.Add(title);
        var hint = Text("CodeIsland reads today and the next 6 days (read only). Use an app-specific password, not your Apple ID password.",
            12, Secondary, Ui);
        hint.TextWrapping = TextWrapping.Wrap;
        hint.Margin = new Thickness(0, 6, 0, 4);
        stack.Children.Add(hint);
        var link = Text("Create one at account.apple.com → Sign-In and Security → App-Specific Passwords", 12, Brush(Link), Ui);
        link.TextWrapping = TextWrapping.Wrap;
        link.Cursor = Cursors.Hand;
        link.MouseLeftButtonUp += (_, _) => Process.Start(new ProcessStartInfo("https://account.apple.com/account/manage") { UseShellExecute = true });
        stack.Children.Add(link);

        var existing = CredentialStore.Read(CredentialStore.CalendarTarget);
        stack.Children.Add(Label("APPLE ID"));
        _appleId = new TextBox { Text = existing?.User ?? string.Empty };
        StyleInput(_appleId);
        stack.Children.Add(_appleId);
        stack.Children.Add(Label("APP-SPECIFIC PASSWORD"));
        _password = new PasswordBox();
        StyleInput(_password);
        stack.Children.Add(_password);

        _status = Text(existing is null ? string.Empty : "Connected. Enter a new password to replace it.", 12, Secondary, Ui);
        _status.TextWrapping = TextWrapping.Wrap;
        _status.Margin = new Thickness(0, 10, 0, 0);
        stack.Children.Add(_status);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        if (existing is not null)
            buttons.Children.Add(Button("Disconnect", White(0.08), Brush(Deny), () =>
            {
                _calendar.Disconnect();
                Close();
            }));
        buttons.Children.Add(Button("Cancel", White(0.08), White(0.8), Close));
        _connect = Button("Connect", White(0.9), Black, () => _ = ConnectAsync());
        buttons.Children.Add(_connect);
        stack.Children.Add(buttons);

        Content = new Border
        {
            Background = Brush(Rgb(0.07, 0.07, 0.08)),
            BorderBrush = White(0.12),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Child = stack
        };
        MouseLeftButtonDown += (_, e) =>
        {
            // DragMove captures the mouse, so a button under the pointer would never see its click.
            if (!IsInteractive(e.OriginalSource as DependencyObject)) DragMove();
        };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
            if (e.Key == Key.Enter) _ = ConnectAsync();
        };
        Loaded += (_, _) => (string.IsNullOrEmpty(_appleId.Text) ? (UIElement)_appleId : _password).Focus();
    }

    private async Task ConnectAsync()
    {
        if (_busy) return;
        var id = _appleId.Text.Trim();
        var password = _password.Password.Replace(" ", string.Empty).Trim();
        if (id.Length == 0 || password.Length == 0)
        {
            _status.Text = "Enter the Apple ID and the app-specific password.";
            _status.Foreground = Brush(QuotaWarning);
            return;
        }
        _busy = true;
        _connect.Opacity = 0.5;
        _status.Text = "Checking with iCloud…";
        _status.Foreground = Secondary;
        var error = await _calendar.ConnectAsync(id, password);
        _busy = false;
        _connect.Opacity = 1;
        if (error is null)
        {
            Close();
            return;
        }
        _status.Text = error;
        _status.Foreground = Brush(Deny);
    }

    private static bool IsInteractive(DependencyObject? element)
    {
        for (; element is not null; element = element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element))
            if (element is TextBox or PasswordBox || element is FrameworkElement { Cursor: not null } fe && fe.Cursor == Cursors.Hand)
                return true;
        return false;
    }

    private static TextBlock Label(string text)
    {
        var label = Text(text, 10.5, Tertiary, Ui, FontWeights.SemiBold);
        label.Margin = new Thickness(0, 14, 0, 5);
        return label;
    }

    private static void StyleInput(System.Windows.Controls.Control control)
    {
        control.Background = White(0.07);
        control.Foreground = Primary;
        control.BorderBrush = White(0.16);
        control.BorderThickness = new Thickness(1);
        control.FontFamily = Ui;
        control.FontSize = 13;
        control.Padding = new Thickness(8, 6, 8, 6);
        if (control is TextBox box) box.CaretBrush = Primary;
        if (control is PasswordBox pass) pass.CaretBrush = Primary;
    }

    private static Border Button(string text, System.Windows.Media.Brush background, System.Windows.Media.Brush foreground, Action action)
    {
        var button = new Border
        {
            CornerRadius = new CornerRadius(8),
            Background = background,
            Padding = new Thickness(14, 6, 14, 7),
            Margin = new Thickness(8, 0, 0, 0),
            Cursor = Cursors.Hand,
            Child = Text(text, 12.5, foreground, Ui, FontWeights.SemiBold, TextTrimming.None)
        };
        button.MouseLeftButtonUp += (_, e) =>
        {
            action();
            e.Handled = true;
        };
        return button;
    }
}
