using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using NotchTasks.Core;
using NotchTasks.Core.Asks;

namespace NotchTasks.Pill;

/// One waiting ask in the open pill (Mac AskCard). An approval shows what the agent wants to run, with Deny and Allow.
/// A question shows its options: one single-choice question is answered by the tap; otherwise the picks go with Send.
/// The pill's timer calls UpdateWaiting once a second while the card is shown.
public partial class AskCard : UserControl
{
    private const string ApprovalGlyph = "";
    private const string QuestionGlyph = "";
    private const string CheckGlyph = "";

    private static readonly Brush OptionOff = Frozen(new SolidColorBrush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush Dim = Frozen(new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush Muted = Frozen(new SolidColorBrush(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush SummaryBox = Frozen(new SolidColorBrush(Color.FromArgb(0x59, 0x00, 0x00, 0x00)));

    private readonly List<HashSet<string>> _picks = [];
    private readonly List<List<Option>> _options = [];

    /// An answer option: its button, and for multi-select the box and tick drawn inside it.
    private sealed record Option(string Label, Button Button, Border? Box, TextBlock? Mark);

    public AskCard(AgentAsk ask)
    {
        InitializeComponent();
        Ask = ask;
        Glyph.Text = GlyphFor(ask);
        TitleText.Text = AskPresentation.Title(ask);
        SessionText.Text = AskPresentation.Display(ask.Session);

        var agent = AskPresentation.Display(AskPresentation.AgentName(ask));
        LinkText.Text = L10n.L("Answer in {0}", agent);
        LinkButton.ToolTip = L10n.L("Skip here, {0} will ask as usual", agent);
        LinkButton.Click += (_, _) => Replied?.Invoke(new AskReply(ReplyKinds.Pass, null));

        if (ask.IsQuestion)
        {
            BuildQuestions();
        }
        else
        {
            BuildApproval();
        }
        UpdateWaiting(DateTimeOffset.UtcNow);
    }

    /// The ask this card answers.
    public AgentAsk Ask { get; }

    /// The reply the user gave: Allow, Deny, Send, or the tap of a single choice. Pass comes from the link.
    public event Action<AskReply>? Replied;

    /// The glyph for an ask's header: a question mark for questions, a warning sign for approvals.
    public static string GlyphFor(AgentAsk ask) => ask.IsQuestion ? QuestionGlyph : ApprovalGlyph;

    /// Shows "Waiting m:ss" for the time since the ask came in.
    public void UpdateWaiting(DateTimeOffset now) => WaitingText.Text = AskPresentation.Waiting(now - Ask.At);

    private void BuildApproval()
    {
        DenyButton.Content = L10n.L("Deny");
        AllowButton.Content = L10n.L("Allow");
        DenyButton.Visibility = Visibility.Visible;
        AllowButton.Visibility = Visibility.Visible;
        DenyButton.Click += (_, _) => Replied?.Invoke(new AskReply(ReplyKinds.Deny, null));
        AllowButton.Click += (_, _) => Replied?.Invoke(new AskReply(ReplyKinds.Allow, null));

        var body = new StackPanel { Margin = new Thickness(0, 9, 0, 0) };
        body.Children.Add(new TextBlock
        {
            Text = AskPresentation.Display(AskParser.ActionLabel(Ask.Tool)),
            FontSize = 10,
            FontWeight = FontWeights.Bold,
            Foreground = (Brush)FindResource("AskOrange"),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        // The whole command, shown through Display (line breaks visible). Five lines are in view at once; the rest scrolls.
        // A summary the parser cut at its limit ends with "… (cut)".
        const double lineHeight = 16;
        var content = new StackPanel();
        content.Children.Add(new ScrollViewer
        {
            MaxHeight = 5 * lineHeight,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = new TextBlock
            {
                Text = AskPresentation.Display(Ask.Summary),
                FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                FontSize = 11,
                LineHeight = lineHeight,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Muted,
            },
        });
        if (AskPresentation.SummaryWasCut(Ask))
        {
            content.Children.Add(new TextBlock
            {
                Text = "… " + L10n.L("(cut)"),
                FontSize = 11,
                Foreground = Dim,
                Margin = new Thickness(0, 4, 0, 0),
            });
        }
        var box = new Border
        {
            Margin = new Thickness(0, 4, 0, 0),
            Padding = new Thickness(8),
            CornerRadius = new CornerRadius(8),
            Background = SummaryBox,
            Child = content,
        };
        body.Children.Add(box);
        BodyPanel.Children.Add(body);
    }

    private void BuildQuestions()
    {
        for (var i = 0; i < Ask.Questions.Count; i++)
        {
            var question = Ask.Questions[i];
            var picks = new HashSet<string>(StringComparer.Ordinal);
            _picks.Add(picks);
            var options = new List<Option>();
            _options.Add(options);

            var block = new StackPanel { Margin = new Thickness(0, i == 0 ? 0 : 9, 0, 0) };
            block.Children.Add(new TextBlock
            {
                Text = AskPresentation.Display(question.Question),
                FontSize = 12,
                FontWeight = FontWeights.Medium,
                TextWrapping = TextWrapping.Wrap,
            });

            // Two columns of options, as the Mac's adaptive grid shows them in this width.
            var grid = new UniformGrid { Columns = 2, Margin = new Thickness(0, 6, 0, 0) };
            var index = i;
            foreach (var label in question.Options)
            {
                var option = BuildOption(label, question.MultiSelect);
                options.Add(option);
                option.Button.Click += (_, _) => Pick(index, label);
                grid.Children.Add(option.Button);
            }
            block.Children.Add(grid);
            BodyPanel.Children.Add(block);
        }

        // One single-choice question is answered by its tap, so the Send button is only for the other shapes.
        SendButton.Content = L10n.L("Send");
        SendButton.IsEnabled = false;
        SendButton.Visibility = AskPresentation.TapSends(Ask.Questions) ? Visibility.Collapsed : Visibility.Visible;
        SendButton.Click += (_, _) => Replied?.Invoke(new AskReply(ReplyKinds.Answers, CurrentAnswers()));
    }

    private Option BuildOption(string label, bool multiSelect)
    {
        var button = new Button
        {
            Style = (Style)FindResource("OptionButton"),
            Focusable = false,  // the pill never takes the keyboard focus: its buttons do not activate or focus the window
            Margin = new Thickness(0, 0, 6, 6),
            Background = OptionOff,
            Foreground = Brushes.White,
        };
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        Border? box = null;
        TextBlock? mark = null;
        if (multiSelect)
        {
            mark = new TextBlock
            {
                Text = string.Empty,
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 9,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Brushes.Black,
            };
            box = new Border
            {
                Width = 12,
                Height = 12,
                CornerRadius = new CornerRadius(2),
                BorderThickness = new Thickness(1),
                BorderBrush = Dim,
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = mark,
            };
            Grid.SetColumn(box, 0);
            row.Children.Add(box);
        }
        // The option shows its Display text; the picked label (the raw one) is what the reply sends back.
        var text = new TextBlock { Text = AskPresentation.Display(label), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        button.Content = row;
        return new Option(label, button, box, mark);
    }

    /// A tap on an option: a single choice replaces the pick; a multi-select option toggles. A single-choice question
    /// with no other question is answered at once.
    private void Pick(int question, string label)
    {
        var multi = Ask.Questions[question].MultiSelect;
        var picks = _picks[question];
        if (multi)
        {
            if (!picks.Remove(label))
            {
                picks.Add(label);
            }
        }
        else
        {
            picks.Clear();
            picks.Add(label);
        }
        foreach (var option in _options[question])
        {
            Look(option, picks.Contains(option.Label));
        }

        if (!multi && AskPresentation.TapSends(Ask.Questions))
        {
            // The reply is the last thing this card does: it may be removed from the pill while it is raised.
            Replied?.Invoke(new AskReply(ReplyKinds.Answers, CurrentAnswers()));
            return;
        }
        SendButton.IsEnabled = AskPresentation.Ready(Ask.Questions, _picks);
    }

    private static void Look(Option option, bool selected)
    {
        option.Button.Background = selected ? Brushes.White : OptionOff;
        option.Button.Foreground = selected ? Brushes.Black : Brushes.White;
        if (option.Box is not null && option.Mark is not null)
        {
            option.Box.BorderBrush = selected ? Brushes.Black : Dim;
            option.Mark.Text = selected ? CheckGlyph : string.Empty;
        }
    }

    private IReadOnlyDictionary<string, string> CurrentAnswers() => AskPresentation.Answers(Ask.Questions, _picks);

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
