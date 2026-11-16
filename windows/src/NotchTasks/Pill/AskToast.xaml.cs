using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using NotchTasks.Core;
using NotchTasks.Core.Asks;

namespace NotchTasks.Pill;

/// The toast an agent's new ask shows at the top of the pill (Mac island toast, 520 wide). Allow and Deny answer the
/// ask here; a question's Reply, or a click on the toast, opens the pill, where every question is answered.
public partial class AskToast : UserControl
{
    private AgentAsk? _ask;

    public AskToast()
    {
        InitializeComponent();
        Root.MouseLeftButtonUp += OnRootClick;
        AllowButton.Click += (_, _) => Answer(ReplyKinds.Allow);
        DenyButton.Click += (_, _) => Answer(ReplyKinds.Deny);
        ReplyButton.Click += (_, _) => Opened?.Invoke();
        HideButton.Click += (_, _) => Hidden?.Invoke();
    }

    /// Allow or Deny was pressed for the ask on the toast.
    public event Action<AgentAsk, AskReply>? Answered;

    /// The pill should open: Reply, or a click on the toast's text.
    public event Action? Opened;

    /// × was pressed: the toast leaves; the ask stays in the pill.
    public event Action? Hidden;

    /// The ask the toast shows, or null before the first Show.
    public AgentAsk? Ask => _ask;

    /// Shows the ask: its glyph, title, subtitle, and the buttons it needs (Reply for a question, Deny and Allow for an approval).
    public void Show(AgentAsk ask)
    {
        _ask = ask;
        Glyph.Text = AskCard.GlyphFor(ask);
        TitleText.Text = AskPresentation.Title(ask);
        SubtitleText.Text = AskPresentation.Subtitle(ask);
        ReplyButton.Content = L10n.L("Reply");
        DenyButton.Content = L10n.L("Deny");
        AllowButton.Content = L10n.L("Allow");
        HideButton.ToolTip = L10n.L("Hide alert");
        ReplyButton.Visibility = ask.IsQuestion ? Visibility.Visible : Visibility.Collapsed;
        ApprovalButtons.Visibility = ask.IsQuestion ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Answer(string kind)
    {
        if (_ask is { } ask)
        {
            Answered?.Invoke(ask, new AskReply(kind, null));
        }
    }

    /// A click on the toast's own surface opens the pill (Mac openToast). A click on a button is left to the button, and
    /// is handled here so that the pill's click handler does not also act on it.
    private void OnRootClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (!IsInsideButton(e.OriginalSource as DependencyObject))
        {
            Opened?.Invoke();
        }
    }

    private static bool IsInsideButton(DependencyObject? source)
    {
        for (var node = source; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is ButtonBase)
            {
                return true;
            }
        }
        return false;
    }
}
