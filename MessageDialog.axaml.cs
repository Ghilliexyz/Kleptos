using System.Threading.Tasks;
using Avalonia.Controls;

namespace Kleptos;

public enum MessageDialogButtons
{
    OK,
    OKCancel,
    YesNo,
}

public enum MessageDialogResult
{
    None,
    OK,
    Cancel,
    Yes,
    No,
}

/// <summary>
/// Minimal dark-themed modal dialog replacing WPF's MessageBox.
/// </summary>
public partial class MessageDialog : Window
{
    public MessageDialog()
    {
        InitializeComponent();
    }

    private MessageDialog(string title, string message, MessageDialogButtons buttons) : this()
    {
        txtTitle.Text = title;
        txtMessage.Text = message;

        switch (buttons)
        {
            case MessageDialogButtons.OK:
                AddButton("OK", MessageDialogResult.OK, primary: true);
                break;
            case MessageDialogButtons.OKCancel:
                AddButton("Cancel", MessageDialogResult.Cancel, primary: false);
                AddButton("OK", MessageDialogResult.OK, primary: true);
                break;
            case MessageDialogButtons.YesNo:
                AddButton("No", MessageDialogResult.No, primary: false);
                AddButton("Yes", MessageDialogResult.Yes, primary: true);
                break;
        }
    }

    private void AddButton(string text, MessageDialogResult result, bool primary)
    {
        var btn = new Button
        {
            Content = text,
            Margin = new Avalonia.Thickness(8, 0, 0, 0),
        };
        btn.Classes.Add(primary ? "Primary" : "Dialog");
        btn.Click += (_, _) => Close(result);
        pnlButtons.Children.Add(btn);
    }

    public static Task<MessageDialogResult> ShowAsync(Window owner, string title, string message, MessageDialogButtons buttons)
    {
        var dlg = new MessageDialog(title, message, buttons);
        return dlg.ShowDialog<MessageDialogResult>(owner);
    }
}
