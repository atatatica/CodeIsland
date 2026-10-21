using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using CodeIsland.Core;
using CodeIsland.Ipc;
using CodeIsland.Windows.Island;
using static CodeIsland.Windows.Island.IslandStyle;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Cursors = System.Windows.Input.Cursors;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;
using Path = System.Windows.Shapes.Path;
using Point = System.Windows.Point;
using Rectangle = System.Windows.Shapes.Rectangle;
using Size = System.Windows.Size;
using TextBox = System.Windows.Controls.TextBox;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace CodeIsland.Windows;

/// The island: a black bar hanging from the top edge that opens on hover into the
/// session list, or the agenda when no agent is running — the macOS notch panel.
public partial class MainWindow : Window
{
    private const double CollapsedHeight = 34;
    private const double ExpandedWidth = 640;
    private const double MaxExpandedHeight = 660;
    private const double CollapsedRadius = 12;
    private const double ExpandedRadius = 22;
    private const double ShoulderSize = 10;
    private static readonly TimeSpan OpenDelay = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan CloseDelay = TimeSpan.FromMilliseconds(500);

    private readonly DesktopSessionStore _store;
    private readonly SessionTracker _tracker;
    private readonly QuotaService _quota;
    private readonly CalendarService _calendar;
    private readonly TerminalActivator _terminalActivator = new();
    private readonly WorkspaceLauncher _workspaceLauncher = new();
    private readonly Dictionary<string, string> _answerDrafts = new(StringComparer.Ordinal);
    private readonly DispatcherTimer _hoverTimer;
    private readonly DispatcherTimer _clockTimer;
    private AppSettings _settings;
    private GlobalHotKeyManager? _hotKeys;
    private HwndSource? _source;

    private Border _island = null!;
    private Grid _content = null!;
    private FrameworkElement? _collapsedView;
    private FrameworkElement? _expandedView;
    private bool _expanded;
    private bool _pinned;
    private bool _agendaInList;
    private string _tab = "all";
    private DateTime? _hoverDay;
    private DateTimeOffset? _enteredAt;
    private DateTimeOffset? _leftAt;
    private bool _renderQueued;
    private bool _modalOpen;

    public string HotKeyStatus => _hotKeys?.RegistrationSummary ?? "Shortcuts are not registered yet.";
    public IntPtr NativeHandle => new WindowInteropHelper(this).Handle;

    public MainWindow(DesktopSessionStore sessions, AppSettings settings)
    {
        InitializeComponent();
        _store = sessions;
        _settings = settings;
        _tracker = new SessionTracker(sessions, Dispatcher);
        _quota = new QuotaService(Dispatcher) { IsBusy = () => _tracker.ActiveCount > 0 };
        _calendar = new CalendarService(Dispatcher);
        _tracker.Changed += (_, _) =>
        {
            _quota.Nudge();
            QueueRender();
        };
        _quota.Changed += (_, _) => QueueRender();
        _calendar.Changed += (_, _) => QueueRender();

        BuildShell();
        _hoverTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(40), DispatcherPriority.Input, (_, _) => TrackPointer(), Dispatcher);
        _clockTimer = new DispatcherTimer(TimeSpan.FromSeconds(30), DispatcherPriority.Background, (_, _) => QueueRender(), Dispatcher);

        SourceInitialized += (_, _) =>
        {
            _source = PresentationSource.FromVisual(this) as HwndSource;
            SetNoActivate(true);
            if (_source is not null) RegisterHotKeys();
            PositionWindow();
        };
        Loaded += (_, _) =>
        {
            Render(animate: false);
            _hoverTimer.Start();
            _clockTimer.Start();
            _quota.Start();
            _calendar.Start();
        };
        Closed += (_, _) =>
        {
            _hotKeys?.Dispose();
            _hoverTimer.Stop();
            _clockTimer.Stop();
            _quota.Dispose();
            _calendar.Dispose();
        };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Collapse();
        };
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (_, _) => Dispatcher.BeginInvoke(PositionWindow);
    }

    // MARK: - App surface

    public void ApplySettings(AppSettings settings)
    {
        _settings = settings;
        PositionWindow();
        if (_source is not null) RegisterHotKeys();
        QueueRender();
    }

    public void TogglePanel()
    {
        if (_expanded) Collapse();
        else
        {
            _pinned = true;
            Expand();
        }
    }

    public void ExpandPanel()
    {
        _pinned = true;
        Expand();
    }

    private void RegisterHotKeys()
    {
        _hotKeys?.Dispose();
        _hotKeys = new GlobalHotKeyManager(_source!, _settings, TogglePanel,
            () => ResolveFirstPending(UserAction.Approve),
            () => ResolveFirstPending(UserAction.Deny));
    }

    private bool ResolveFirstPending(UserAction action)
    {
        var session = _tracker.Sessions.FirstOrDefault(s => s.Status == CardStatus.NeedsYou && s.PendingEventId is not null);
        return session is not null && _tracker.Resolve(session, action);
    }

    // MARK: - Window placement and hover

    private void PositionWindow()
    {
        var screen = DisplayPositioner.SelectWorkingArea(_settings.DisplayMode);
        var bounds = System.Windows.Forms.Screen.FromRectangle(screen).Bounds;
        // GetDpi is right as soon as the HWND exists; TransformToDevice can still be identity then.
        var dpi = VisualTreeHelper.GetDpi(this);
        var sx = dpi.DpiScaleX;
        var sy = dpi.DpiScaleY;
        Left = bounds.Left / sx + (bounds.Width / sx - Width) / 2;
        Top = bounds.Top / sy;
    }

    /// Polls the pointer: the bar opens after resting on it (or the screen edge above it)
    /// for 0.3 s and closes 0.5 s after the pointer leaves the open island.
    private void TrackPointer()
    {
        if (!IsVisible || _source is null) return;
        if (!GetCursorPos(out var cursor)) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        var transform = new Matrix(dpi.DpiScaleX, 0, 0, dpi.DpiScaleY, 0, 0);
        var islandOrigin = _island.PointToScreen(new Point(0, 0));
        var w = _island.ActualWidth * transform.M11;
        var h = _island.ActualHeight * transform.M22;
        var now = DateTimeOffset.UtcNow;

        bool inside;
        if (_expanded)
        {
            var margin = 10 * transform.M11;
            inside = cursor.X >= islandOrigin.X - margin && cursor.X <= islandOrigin.X + w + margin
                && cursor.Y >= islandOrigin.Y - margin && cursor.Y <= islandOrigin.Y + h + margin;
        }
        else
        {
            var side = (ShoulderSize + 14) * transform.M11;
            inside = cursor.X >= islandOrigin.X - side && cursor.X <= islandOrigin.X + w + side
                && cursor.Y >= islandOrigin.Y - 2 && cursor.Y <= islandOrigin.Y + h + 4 * transform.M22;
        }

        if (!_expanded)
        {
            if (!_settings.HoverToExpand || !inside)
            {
                _enteredAt = null;
                return;
            }
            _enteredAt ??= now;
            if (now - _enteredAt >= OpenDelay)
            {
                _enteredAt = null;
                Expand();
            }
            return;
        }

        if (inside || _pinned || _modalOpen || IsEditing())
        {
            _leftAt = null;
            if (inside) _pinned = false; // once the pointer visits, leaving closes as usual
            return;
        }
        _leftAt ??= now;
        if (now - _leftAt >= CloseDelay)
        {
            _leftAt = null;
            Collapse();
        }
    }

    private bool IsEditing() => Keyboard.FocusedElement is TextBox { Text.Length: > 0 };

    private void Expand()
    {
        if (_expanded) return;
        _expanded = true;
        _leftAt = null;
        _calendar.Activate();
        Render(animate: true);
    }

    private void Collapse()
    {
        if (!_expanded) return;
        _expanded = false;
        _pinned = false;
        _agendaInList = false;
        _hoverDay = null;
        SetNoActivate(true);
        Render(animate: true);
    }

    // MARK: - Shell

    private void BuildShell()
    {
        _island = new Border
        {
            Background = Black,
            Height = CollapsedHeight,
            Width = 240,
            ClipToBounds = true,
            VerticalAlignment = VerticalAlignment.Top,
            Cursor = Cursors.Arrow
        };
        _island.SizeChanged += (_, _) => UpdateClip();
        _content = new Grid { VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Center };
        _island.Child = _content;
        _island.MouseLeftButtonUp += (_, e) =>
        {
            if (!_expanded)
            {
                _pinned = true;
                Expand();
                e.Handled = true;
            }
        };

        var host = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top
        };
        host.Children.Add(Shoulder(mirror: false));
        host.Children.Add(_island);
        host.Children.Add(Shoulder(mirror: true));
        Root.Children.Add(host);
    }

    /// The concave corner that blends the island into the screen edge.
    private static Path Shoulder(bool mirror)
    {
        var s = ShoulderSize;
        var figure = new PathFigure { StartPoint = new Point(0, 0), IsClosed = true };
        figure.Segments.Add(new LineSegment(new Point(s, 0), false));
        figure.Segments.Add(new LineSegment(new Point(s, s), false));
        figure.Segments.Add(new ArcSegment(new Point(0, 0), new Size(s, s), 0, false, SweepDirection.Counterclockwise, false));
        var geometry = new PathGeometry([figure]);
        geometry.Freeze();
        return new Path
        {
            Data = geometry,
            Fill = Black,
            Width = s,
            Height = s,
            VerticalAlignment = VerticalAlignment.Top,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = mirror ? new ScaleTransform(-1, 1) : Transform.Identity
        };
    }

    private void UpdateClip()
    {
        var w = _island.ActualWidth;
        var h = _island.ActualHeight;
        var t = Math.Clamp((h - CollapsedHeight) / 200, 0, 1);
        var r = CollapsedRadius + (ExpandedRadius - CollapsedRadius) * t;
        // Square top corners (they meet the screen edge), round bottom ones.
        _island.Clip = new RectangleGeometry(new Rect(0, -r, w, h + r), r, r);
    }

    // MARK: - Rendering

    private void QueueRender()
    {
        if (_renderQueued) return;
        _renderQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _renderQueued = false;
            // Rebuilding would drop the caret of an answer being typed.
            if (IsEditing()) return;
            Render(animate: true);
        });
    }

    private void Render(bool animate)
    {
        var collapsed = BuildCollapsed();
        // A steady width, like the notch: the bar must not twitch with every tool call.
        collapsed.Measure(new Size(double.PositiveInfinity, CollapsedHeight));
        var minimum = _tracker.TotalCount > 0 ? 440 : _quota.Snapshot is not null ? 340 : 280;
        var collapsedWidth = Math.Clamp(Math.Ceiling(collapsed.DesiredSize.Width), minimum, 470);

        _content.Children.Clear();
        _collapsedView = collapsed;
        _expandedView = null;
        double targetWidth, targetHeight;
        if (_expanded)
        {
            var expanded = BuildExpanded();
            expanded.Width = ExpandedWidth;
            expanded.Measure(new Size(ExpandedWidth, double.PositiveInfinity));
            targetWidth = ExpandedWidth;
            targetHeight = Math.Min(MaxExpandedHeight, Math.Ceiling(expanded.DesiredSize.Height));
            expanded.MaxHeight = MaxExpandedHeight;
            _expandedView = expanded;
            _content.Children.Add(expanded);
            if (animate) FadeIn(expanded, 0.05);
        }
        else
        {
            collapsed.Width = collapsedWidth;
            targetWidth = collapsedWidth;
            targetHeight = CollapsedHeight;
            _content.Children.Add(collapsed);
            if (animate) FadeIn(collapsed, 0.08);
        }
        AnimateIsland(targetWidth, targetHeight, animate);
    }

    private void AnimateIsland(double width, double height, bool animate)
    {
        var sameSize = Math.Abs(_island.Width - width) < 0.5 && Math.Abs(_island.Height - height) < 0.5;
        if (!animate || !SystemParameters.ClientAreaAnimation)
        {
            _island.BeginAnimation(WidthProperty, null);
            _island.BeginAnimation(HeightProperty, null);
            _island.Width = width;
            _island.Height = height;
            return;
        }
        if (sameSize && !_island.HasAnimatedProperties) return;
        var opening = height > _island.ActualHeight + 1;
        var ease = opening ? new SpringEase { Response = 0.45, Damping = 0.8 } : new SpringEase { Response = 0.32, Damping = 0.92 };
        var duration = SpringDuration(ease.Response);
        var wa = new DoubleAnimation(width, duration) { EasingFunction = ease };
        var ha = new DoubleAnimation(height, duration) { EasingFunction = ease };
        wa.Completed += (_, _) => { _island.BeginAnimation(WidthProperty, null); _island.Width = width; };
        ha.Completed += (_, _) => { _island.BeginAnimation(HeightProperty, null); _island.Height = height; };
        _island.Width = width;
        _island.Height = height;
        _island.BeginAnimation(WidthProperty, wa, HandoffBehavior.SnapshotAndReplace);
        _island.BeginAnimation(HeightProperty, ha, HandoffBehavior.SnapshotAndReplace);
    }

    private static void FadeIn(UIElement element, double delay)
    {
        if (!SystemParameters.ClientAreaAnimation) return;
        element.Opacity = 0;
        element.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180))
        {
            BeginTime = TimeSpan.FromSeconds(delay),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
    }

    // MARK: - Collapsed bar

    private FrameworkElement BuildCollapsed()
    {
        var focus = _tracker.Focus;
        var row = new DockPanel { Height = CollapsedHeight, LastChildFill = true, Margin = new Thickness(10, 0, 10, 0) };

        // Right wing: waiting badge, session count, weekly usage.
        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        if (_tracker.AnyNeedsYou)
        {
            var bell = Icon("", 11, Brush(QuotaWarning));
            bell.Margin = new Thickness(0, 0, 7, 0);
            Pulse(bell);
            right.Children.Add(bell);
        }
        if (_tracker.TotalCount > 0) right.Children.Add(SessionCount());
        if (_quota.Snapshot?.Get(QuotaKind.WeeklyAll) is { } weekly)
        {
            var chip = QuotaRingChip(weekly, "W");
            chip.Margin = new Thickness(_tracker.TotalCount > 0 ? 9 : 0, 0, 0, 0);
            right.Children.Add(chip);
        }
        DockPanel.SetDock(right, Dock.Right);
        row.Children.Add(right);

        // Left wing: mascot, then the 5-hour chip.
        var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var mascot = new Mascot(focus?.Agent ?? AgentKind.Claude, 20, focus?.IsActive == true)
        {
            Opacity = focus is null ? 0.55 : 1,
            Margin = new Thickness(0, 0, 8, 0)
        };
        left.Children.Add(mascot);
        if (_quota.Snapshot?.Get(QuotaKind.Session) is { } session)
        {
            var chip = QuotaRingChip(session, "5h");
            chip.Margin = new Thickness(0, 0, 12, 0);
            left.Children.Add(chip);
        }
        else if (_quota.State == QuotaState.NeedsLogin)
        {
            var login = Text("login", 10.5, Brush(QuotaWarning), Mono, FontWeights.SemiBold);
            login.Margin = new Thickness(0, 0, 12, 0);
            login.ToolTip = "Claude Code login expired: run claude once to refresh it.";
            left.Children.Add(login);
        }
        DockPanel.SetDock(left, Dock.Left);
        row.Children.Add(left);

        // Centre: what the focused agent is doing, or the next event.
        row.Children.Add(CollapsedStatus(focus));
        return row;
    }

    private FrameworkElement CollapsedStatus(SessionInfo? focus)
    {
        if (focus is null)
        {
            var now = DateTime.Now;
            if (_calendar.FocusEvent(now) is { } e && (e.IsOngoing(now) || (e.Start - now).TotalHours < 6))
            {
                var line = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                line.Children.Add(new Ellipse { Width = 6, Height = 6, Fill = Brush(e.Color), Margin = new Thickness(0, 0, 6, 0) });
                line.Children.Add(Text(e.Title, 11.5, Primary, Ui, FontWeights.Medium));
                var countdown = Text("  " + Countdown(e, now), 11, Brush(e.Color), Display, FontWeights.SemiBold, TextTrimming.None);
                line.Children.Add(countdown);
                line.MaxWidth = 260;
                return line;
            }
            var date = Text(now.ToString("ddd d MMM"), 11.5, Secondary, Display, FontWeights.SemiBold);
            return date;
        }

        var color = StatusColor(focus.Status);
        var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 240 };
        string text;
        if (focus.Status == CardStatus.NeedsYou)
            text = focus.PendingTool is { } t ? $"{CompactTool(t)} needs approval" : "waiting for you";
        else if (focus.Tool is { } tool)
            text = focus.ToolDetail is { } d ? $"{CompactTool(tool)} {d}" : CompactTool(tool);
        else if (focus.Status == CardStatus.Thinking)
            text = "thinking…";
        else
            text = focus.Project;
        var label = Text(text, 11.5, focus.IsActive ? Brush(color) : Secondary, Mono, FontWeights.SemiBold);
        panel.Children.Add(label);
        return panel;
    }

    private FrameworkElement SessionCount()
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var active = _tracker.ActiveCount;
        if (active > 0)
        {
            panel.Children.Add(Text(active.ToString(), 12.5, Brush(ActiveCount), Mono, FontWeights.Bold, TextTrimming.None));
            panel.Children.Add(Text("/", 12.5, White(0.5), Mono, FontWeights.Bold, TextTrimming.None));
        }
        panel.Children.Add(Text(_tracker.TotalCount.ToString(), 12.5, White(0.9), Mono, FontWeights.Bold, TextTrimming.None));
        panel.ToolTip = $"{active} active / {_tracker.TotalCount} sessions";
        return panel;
    }

    /// "5h ◔ 72%" — the macOS wing chip: label, ring, percent.
    private FrameworkElement QuotaRingChip(QuotaLimit limit, string label)
    {
        var stale = _quota.State == QuotaState.Error;
        var color = limit.Color;
        var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Opacity = stale ? 0.55 : 1 };
        var caption = Text(label, 10, White(0.45), Mono, FontWeights.Medium, TextTrimming.None);
        caption.Margin = new Thickness(0, 0, 4, 0);
        panel.Children.Add(caption);
        var ring = Ring(limit.Percent / 100, color, 10, 2);
        ring.Margin = new Thickness(0, 0, 4, 0);
        panel.Children.Add(ring);
        panel.Children.Add(Text(QuotaFormat.Percent(limit.Percent), 11, Brush(color), Mono, FontWeights.SemiBold, TextTrimming.None));
        var now = DateTimeOffset.UtcNow;
        var reset = limit.ResetsAt is { } r ? QuotaFormat.Countdown(r, now) : null;
        panel.ToolTip = $"{(limit.Kind == QuotaKind.Session ? "5-hour window" : "Weekly limit")}: {QuotaFormat.Percent(limit.Percent)} used" +
            (reset is null ? string.Empty : $", resets in {reset}") + (stale ? " (stale)" : string.Empty);
        return panel;
    }

    private static FrameworkElement Ring(double fraction, Color color, double size, double thickness)
    {
        var grid = new Grid { Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center };
        grid.Children.Add(new Ellipse { Stroke = White(0.18), StrokeThickness = thickness });
        fraction = Math.Clamp(fraction, 0, 1);
        if (fraction > 0.001)
        {
            var r = (size - thickness) / 2;
            var c = size / 2;
            if (fraction >= 0.999)
                grid.Children.Add(new Ellipse { Stroke = Brush(color), StrokeThickness = thickness });
            else
            {
                var angle = fraction * 2 * Math.PI;
                var end = new Point(c + r * Math.Sin(angle), c - r * Math.Cos(angle));
                var figure = new PathFigure { StartPoint = new Point(c, c - r) };
                figure.Segments.Add(new ArcSegment(end, new Size(r, r), 0, fraction > 0.5, SweepDirection.Clockwise, true));
                grid.Children.Add(new Path
                {
                    Data = new PathGeometry([figure]),
                    Stroke = Brush(color),
                    StrokeThickness = thickness,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round
                });
            }
        }
        return grid;
    }

    // MARK: - Expanded panel

    private FrameworkElement BuildExpanded()
    {
        var showAgenda = _tracker.TotalCount == 0 || _agendaInList;
        var stack = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };
        stack.Children.Add(Header(showAgenda));
        stack.Children.Add(new Border
        {
            Height = 1,
            Margin = new Thickness(14, 0, 14, 6),
            BorderBrush = White(0.14),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = null
        });

        if (showAgenda) stack.Children.Add(Agenda());
        else
        {
            var list = SessionList();
            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                MaxHeight = MaxExpandedHeight - 130,
                Content = list
            };
            stack.Children.Add(scroll);
            if (NextEventStrip() is { } strip) stack.Children.Add(strip);
        }
        stack.Children.Add(Footer());
        return stack;
    }

    private FrameworkElement Header(bool showAgenda)
    {
        var grid = new Grid { Height = 42, Margin = new Thickness(14, 2, 10, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        // The pill: a small "battery" mark like the macOS header.
        var pill = new Border
        {
            Width = 22,
            Height = 12,
            CornerRadius = new CornerRadius(6),
            Background = White(0.22),
            Margin = new Thickness(0, 0, 12, 0),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new Rectangle { Width = 4, Height = 4, Fill = Brush(Rgb(1, 0.78, 0.25)), Margin = new Thickness(0, 0, 3, 0) },
                    new Rectangle { Width = 4, Height = 4, Fill = Brush(Rgb(1, 0.78, 0.25)) }
                }
            }
        };
        left.Children.Add(pill);
        if (!showAgenda) left.Children.Add(Tabs());
        grid.Children.Add(left);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(buttons, 2);
        if (_tracker.TotalCount > 0)
            buttons.Children.Add(IconButton(_agendaInList ? "" : "", _agendaInList ? "Sessions" : "Calendar", White(0.85), () =>
            {
                _agendaInList = !_agendaInList;
                Render(animate: true);
            }));
        buttons.Children.Add(IconButton(_settings.SoundEnabled ? "" : "", _settings.SoundEnabled ? "Mute" : "Unmute", White(0.85), () =>
        {
            var enabled = ((App)System.Windows.Application.Current).ToggleSound();
            _settings = _settings with { SoundEnabled = enabled };
            Render(animate: false);
        }));
        buttons.Children.Add(IconButton("", "Settings", White(0.85), () => ((App)System.Windows.Application.Current).OpenSettings()));
        buttons.Children.Add(IconButton("", "Quit CodeIsland", Brush(Rgb(1, 0.35, 0.35)), () => ((App)System.Windows.Application.Current).RequestExit()));
        grid.Children.Add(buttons);
        return grid;
    }

    private FrameworkElement Tabs()
    {
        var border = new Border
        {
            BorderBrush = White(0.18),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Background = White(0.04)
        };
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (key, label) in new[] { ("all", "ALL"), ("status", "STATUS"), ("agent", "AGENT") })
        {
            var selected = _tab == key;
            var tab = new Border
            {
                Padding = new Thickness(9, 3, 9, 4),
                Background = selected ? White(0.10) : System.Windows.Media.Brushes.Transparent,
                Cursor = Cursors.Hand,
                Child = Text(label, 11, selected ? Brush(Working) : White(0.6), Mono, FontWeights.Bold, TextTrimming.None)
            };
            tab.MouseLeftButtonUp += (_, e) =>
            {
                _tab = key;
                Render(animate: false);
                e.Handled = true;
            };
            panel.Children.Add(tab);
        }
        border.Child = panel;
        return border;
    }

    private static FrameworkElement IconButton(string glyph, string tooltip, Brush brush, Action action)
    {
        var button = new Border
        {
            Width = 30,
            Height = 30,
            CornerRadius = new CornerRadius(15),
            Background = White(0.08),
            Margin = new Thickness(6, 0, 0, 0),
            Cursor = Cursors.Hand,
            ToolTip = tooltip,
            Child = new Grid { Children = { Center(Icon(glyph, 13, brush)) } }
        };
        button.MouseEnter += (_, _) => button.Background = White(0.16);
        button.MouseLeave += (_, _) => button.Background = White(0.08);
        button.MouseLeftButtonUp += (_, e) =>
        {
            action();
            e.Handled = true;
        };
        return button;
    }

    private static T Center<T>(T element) where T : FrameworkElement
    {
        element.HorizontalAlignment = HorizontalAlignment.Center;
        element.VerticalAlignment = VerticalAlignment.Center;
        return element;
    }

    // MARK: Session list

    private FrameworkElement SessionList()
    {
        var panel = new StackPanel();
        var sessions = _tracker.Sessions;
        switch (_tab)
        {
            case "status":
                foreach (var group in sessions.GroupBy(s => StatusLabel(s.Status)))
                {
                    panel.Children.Add(GroupHeader($"{group.Key} ({group.Count()})", StatusColor(group.First().Status)));
                    foreach (var s in group) panel.Children.Add(Card(s));
                }
                break;
            case "agent":
                foreach (var group in sessions.GroupBy(s => s.Agent))
                {
                    panel.Children.Add(GroupHeader($"{group.Key} ({group.Count()})", Reply));
                    foreach (var s in group) panel.Children.Add(Card(s));
                }
                break;
            default:
                foreach (var s in sessions) panel.Children.Add(Card(s));
                break;
        }
        return panel;
    }

    private static FrameworkElement GroupHeader(string text, Color color)
    {
        var block = Text(text, 10.5, Brush(color), Mono, FontWeights.Bold, TextTrimming.None);
        block.Margin = new Thickness(18, 4, 0, 4);
        return block;
    }

    private FrameworkElement Card(SessionInfo s)
    {
        var color = StatusColor(s.Status);
        var grid = new Grid { Margin = new Thickness(14, 10, 14, 10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var mascot = new Mascot(s.Agent, 32, s.IsActive) { VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left };
        grid.Children.Add(mascot);

        var body = new StackPanel();
        Grid.SetColumn(body, 1);
        body.Children.Add(CardHeader(s, color));

        if (s.Status == CardStatus.NeedsYou && s.PendingEventId is not null)
            body.Children.Add(s.PendingIsQuestion ? QuestionBlock(s) : ApprovalBlock(s));
        else if (s.Status == CardStatus.NeedsYou)
            body.Children.Add(GlyphLine("?", NeedsYou, $"Waiting in {s.Host ?? "the terminal"}", NeedsYou, 1));

        if (s.Prompt is { } prompt) body.Children.Add(GlyphLine("›", Rgb(1, 1, 1, 0.5), prompt, Rgb(1, 1, 1, 0.62), 1));
        if (s.Reply is { } reply) body.Children.Add(GlyphLine("●", Reply, reply, Rgb(1, 1, 1, 0.85), s.IsActive ? 1 : 2, glyphSize: 9));

        if (s.Status is CardStatus.Working or CardStatus.Thinking)
        {
            if (s.Tool is { } tool)
            {
                var live = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 0) };
                live.Children.Add(Chip("▸ " + CompactTool(tool), color, 11, 0.12, radius: 3));
                if (s.ToolDetail is { } detail)
                {
                    var d = Text(detail, 12, White(0.75), Mono);
                    d.Margin = new Thickness(7, 0, 0, 0);
                    d.MaxWidth = 470;
                    live.Children.Add(d);
                }
                body.Children.Add(live);
            }
            else body.Children.Add(Thinking());
        }
        grid.Children.Add(body);

        var card = new Border
        {
            CornerRadius = new CornerRadius(10),
            Background = White(0.05),
            Margin = new Thickness(6, 0, 6, 6),
            Cursor = Cursors.Hand,
            Child = new Grid
            {
                Children =
                {
                    new Rectangle { Width = 3, Fill = Brush(RailColor(s.Status)), HorizontalAlignment = HorizontalAlignment.Left },
                    grid
                }
            },
            ClipToBounds = true
        };
        card.Loaded += (_, _) => card.Clip = new RectangleGeometry(new Rect(0, 0, card.ActualWidth, card.ActualHeight), 10, 10);
        card.SizeChanged += (_, _) => card.Clip = new RectangleGeometry(new Rect(0, 0, card.ActualWidth, card.ActualHeight), 10, 10);
        card.MouseEnter += (_, _) => card.Background = White(0.10);
        card.MouseLeave += (_, _) => card.Background = White(0.05);
        card.MouseLeftButtonUp += (_, e) =>
        {
            if (e.Handled) return;
            Jump(s);
        };
        var menu = new ContextMenu();
        var dismiss = new MenuItem { Header = "Dismiss session" };
        dismiss.Click += (_, _) => _tracker.Dismiss(s);
        menu.Items.Add(dismiss);
        menu.Opened += (_, _) => _modalOpen = true;
        menu.Closed += (_, _) => _modalOpen = false;
        card.ContextMenu = menu;
        return card;
    }

    private FrameworkElement CardHeader(SessionInfo s, Color color)
    {
        var dock = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 4) };
        var tags = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        if (s.Subagents > 0) tags.Children.Add(Pill($"+{s.Subagents} Sub", Rgb(0.65, 0.55, 0.95)));
        tags.Children.Add(Pill(Elapsed(s.StartedAt), Rgb(1, 1, 1, 0.8), fill: 0.08));
        if (s.Host is { } host)
        {
            var hostText = Text(host, 11.5, White(0.6), Mono, FontWeights.Medium, TextTrimming.None);
            hostText.Margin = new Thickness(8, 0, 0, 0);
            tags.Children.Add(hostText);
        }
        DockPanel.SetDock(tags, Dock.Right);
        dock.Children.Add(tags);

        var line = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        line.Children.Add(Chip(StatusLabel(s.Status), color, 10, 0.15));
        var project = Text(s.Project, 14, White(0.95), Mono, FontWeights.Bold);
        project.Margin = new Thickness(8, 0, 0, 0);
        project.MaxWidth = 220;
        line.Children.Add(project);
        if (s.Branch is { } branch)
        {
            var b = Text("  ⑂ " + branch, 12.5, White(0.62), Mono);
            b.MaxWidth = 170;
            line.Children.Add(b);
        }
        line.Children.Add(Text($" #{s.ShortId}", 12.5, White(0.4), Mono, trimming: TextTrimming.None));
        dock.Children.Add(line);
        return dock;
    }

    private static FrameworkElement Pill(string text, Color color, double fill = 0.15)
    {
        var tag = new Border
        {
            CornerRadius = new CornerRadius(5),
            Background = fill < 0.1 ? White(fill) : WithOpacity(color, fill),
            Padding = new Thickness(6, 2, 6, 3),
            Margin = new Thickness(4, 0, 0, 0),
            Child = Text(text, 11.5, Brush(color), Mono, FontWeights.SemiBold, TextTrimming.None)
        };
        return tag;
    }

    private static FrameworkElement GlyphLine(string glyph, Color glyphColor, string text, Color textColor, int lines, double glyphSize = 12.5)
    {
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var g = Text(glyph, glyphSize, Brush(glyphColor), Mono, FontWeights.Bold, TextTrimming.None);
        g.VerticalAlignment = VerticalAlignment.Top;
        g.Margin = new Thickness(0, glyphSize < 12 ? 3 : 0, 0, 0);
        grid.Children.Add(g);
        var t = Text(text, 12.5, Brush(textColor), Mono);
        if (lines > 1)
        {
            t.TextWrapping = TextWrapping.Wrap;
            t.MaxHeight = 12.5 * 1.45 * lines;
        }
        Grid.SetColumn(t, 1);
        grid.Children.Add(t);
        return grid;
    }

    private static FrameworkElement Thinking()
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(14, 3, 0, 0) };
        panel.Children.Add(Text("thinking", 12, White(0.55), Mono, trimming: TextTrimming.None));
        for (var i = 0; i < 3; i++)
        {
            var dot = Text(".", 12, White(0.55), Mono, FontWeights.Bold, TextTrimming.None);
            if (SystemParameters.ClientAreaAnimation)
                dot.BeginAnimation(OpacityProperty, new DoubleAnimation(0.2, 1, TimeSpan.FromMilliseconds(450))
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    BeginTime = TimeSpan.FromMilliseconds(i * 150)
                });
            panel.Children.Add(dot);
        }
        return panel;
    }

    private FrameworkElement ApprovalBlock(SessionInfo s)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 2, 0, 4) };
        var request = new StackPanel { Orientation = Orientation.Horizontal };
        request.Children.Add(Chip("▸ " + CompactTool(s.PendingTool ?? "Tool"), NeedsYou, 11, 0.12, radius: 3));
        if (s.PendingDetail is { } detail)
        {
            var d = Text(detail, 12, White(0.82), Mono);
            d.Margin = new Thickness(7, 0, 0, 0);
            d.MaxWidth = 470;
            request.Children.Add(d);
        }
        panel.Children.Add(request);

        var actions = new DockPanel { Margin = new Thickness(0, 7, 0, 0), LastChildFill = false };
        if (s.PendingHasSuggestion)
        {
            var always = InlineButton($"Always allow {CompactTool(s.PendingTool ?? "this")}", ButtonKind.Link,
                () => _tracker.Resolve(s, UserAction.AlwaysAllow));
            DockPanel.SetDock(always, Dock.Left);
            actions.Children.Add(always);
        }
        var allow = InlineButton("Allow once", ButtonKind.Primary, () => _tracker.Resolve(s, UserAction.Approve));
        var deny = InlineButton("Deny", ButtonKind.Destructive, () => _tracker.Resolve(s, UserAction.Deny));
        DockPanel.SetDock(allow, Dock.Right);
        DockPanel.SetDock(deny, Dock.Right);
        actions.Children.Add(allow);
        actions.Children.Add(deny);
        panel.Children.Add(actions);
        return panel;
    }

    private FrameworkElement QuestionBlock(SessionInfo s)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 2, 0, 4) };
        if (s.PendingDetail is { } question)
            panel.Children.Add(GlyphLine("?", NeedsYou, question, Rgb(1, 1, 1, 0.85), 3));
        var row = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        var send = InlineButton("Send", ButtonKind.Primary, () =>
        {
            if (_answerDrafts.TryGetValue(s.SessionId, out var answer) && !string.IsNullOrWhiteSpace(answer)
                && _tracker.Resolve(s, UserAction.Answer, answer))
                _answerDrafts.Remove(s.SessionId);
        });
        DockPanel.SetDock(send, Dock.Right);
        row.Children.Add(send);
        var box = new TextBox
        {
            Text = _answerDrafts.GetValueOrDefault(s.SessionId, string.Empty),
            Background = White(0.07),
            Foreground = Primary,
            CaretBrush = Primary,
            BorderBrush = White(0.16),
            BorderThickness = new Thickness(1),
            FontFamily = Mono,
            FontSize = 12.5,
            Padding = new Thickness(6, 4, 6, 4),
            Margin = new Thickness(0, 0, 8, 0)
        };
        box.TextChanged += (_, _) => _answerDrafts[s.SessionId] = box.Text;
        box.PreviewMouseDown += (_, _) =>
        {
            // Typing needs keyboard focus, which a no-activate window never gets.
            SetNoActivate(false);
            Activate();
            box.Focus();
        };
        row.Children.Add(box);
        panel.Children.Add(row);
        return panel;
    }

    private enum ButtonKind { Primary, Destructive, Quiet, Link }

    private static FrameworkElement InlineButton(string title, ButtonKind kind, Action action)
    {
        var (fg, bg) = kind switch
        {
            ButtonKind.Primary => ((Brush)White(1), (Brush)Brush(Allow)),
            ButtonKind.Destructive => (Brush(Deny), WithOpacity(Deny, 0.14)),
            ButtonKind.Link => (Brush(Link), System.Windows.Media.Brushes.Transparent),
            _ => (White(0.75), White(0.08))
        };
        var button = new Border
        {
            CornerRadius = new CornerRadius(6),
            Background = bg,
            Padding = kind == ButtonKind.Link ? new Thickness(0, 4, 4, 4) : new Thickness(12, 4, 12, 5),
            Margin = new Thickness(kind == ButtonKind.Link ? 0 : 8, 0, 0, 0),
            Cursor = Cursors.Hand,
            Child = Text(title, 12, fg, Mono, FontWeights.Bold, TextTrimming.None)
        };
        button.MouseEnter += (_, _) => button.Opacity = 0.85;
        button.MouseLeave += (_, _) => button.Opacity = 1;
        button.MouseLeftButtonUp += (_, e) =>
        {
            action();
            e.Handled = true;
        };
        return button;
    }

    private void Jump(SessionInfo s)
    {
        // Focus only, as on macOS: never open a new editor or terminal for a click.
        if (_workspaceLauncher.TryLaunchConversation(s.Agent, s.Host, s.SessionId)) return;
        _terminalActivator.TryActivate(s.ProcessId, s.Cwd);
    }

    private FrameworkElement Footer()
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(18, 6, 18, 4) };
        panel.Children.Add(Icon("", 11, White(0.5)));
        var parts = new List<string>();
        var snapshot = _quota.Snapshot;
        var now = DateTimeOffset.UtcNow;
        var title = Text(" Claude", 12, White(0.75), Mono, FontWeights.SemiBold, TextTrimming.None);
        panel.Children.Add(title);
        if (_quota.State == QuotaState.NeedsLogin && snapshot is null)
        {
            panel.Children.Add(Text("  ·  login expired, run claude to refresh", 12, Brush(QuotaWarning), Mono));
            return panel;
        }
        if (snapshot is null)
        {
            panel.Children.Add(Text("  ·  loading plan usage…", 12, White(0.45), Mono));
            return panel;
        }
        foreach (var limit in new[] { snapshot.Get(QuotaKind.Session), snapshot.Get(QuotaKind.WeeklyAll), snapshot.Get(QuotaKind.WeeklyScoped) })
        {
            if (limit is null) continue;
            var label = limit.Kind switch
            {
                QuotaKind.Session => "5h",
                QuotaKind.WeeklyAll => "week",
                _ => limit.ScopeLabel ?? "model"
            };
            panel.Children.Add(Text("  ·  " + label + " ", 12, White(0.55), Mono, trimming: TextTrimming.None));
            panel.Children.Add(Text(QuotaFormat.Percent(limit.Percent), 12, Brush(limit.Color), Mono, FontWeights.SemiBold, TextTrimming.None));
            if (limit.ResetsAt is { } r && QuotaFormat.Countdown(r, now) is { } c)
                panel.Children.Add(Text($" ↻{c}", 12, White(0.4), Mono, trimming: TextTrimming.None));
        }
        if (_quota.State == QuotaState.Error) panel.Opacity = 0.6;
        return panel;
    }

    // MARK: Agenda

    private FrameworkElement Agenda()
    {
        var now = DateTime.Now;
        var selected = _hoverDay ?? now.Date;
        var panel = new StackPanel { Margin = new Thickness(14, 4, 14, 4) };

        var header = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 10) };
        var date = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        date.Children.Add(Text(selected.Day.ToString(), 30, Primary, Display, FontWeights.SemiBold, TextTrimming.None));
        var dateText = new StackPanel { Margin = new Thickness(8, 4, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        dateText.Children.Add(Text(Capitalize(selected.ToString("dddd")), 12, Primary, Ui, FontWeights.SemiBold, TextTrimming.None));
        dateText.Children.Add(Text(selected.ToString("M"), 11, Secondary, Ui, trimming: TextTrimming.None));
        date.Children.Add(dateText);
        DockPanel.SetDock(date, Dock.Left);
        header.Children.Add(date);
        var strip = WeekStrip(selected, now.Date);
        DockPanel.SetDock(strip, Dock.Right);
        header.Children.Add(strip);
        panel.Children.Add(header);

        switch (_calendar.Access)
        {
            case CalendarAccess.NotConfigured:
                panel.Children.Add(AccessCard("", "Show your iCloud events here?", "Connect", OpenCalendarSetup));
                break;
            case CalendarAccess.Connecting when _calendar.Events.Count == 0:
                panel.Children.Add(EmptyState("Loading calendar…"));
                break;
            case CalendarAccess.Failed when _calendar.Events.Count == 0:
                panel.Children.Add(AccessCard("", _calendar.LastError ?? "Calendar unavailable.", "Fix", OpenCalendarSetup));
                break;
            default:
                DayContent(panel, selected, now);
                break;
        }
        return panel;
    }

    private void DayContent(StackPanel panel, DateTime selected, DateTime now)
    {
        var isToday = selected.Date == now.Date;
        var all = _calendar.EventsOn(selected);
        var allDay = all.Where(e => e.IsAllDay).ToArray();
        var timed = all.Where(e => !e.IsAllDay && (!isToday || e.End > now)).ToArray();
        var hero = isToday ? timed.FirstOrDefault() : null;
        var rest = timed.Skip(hero is null ? 0 : 1).ToArray();

        if (allDay.Length > 0) panel.Children.Add(AllDayRow(allDay));
        if (hero is not null) panel.Children.Add(HeroCard(hero, now));
        foreach (var e in rest.Take(4)) panel.Children.Add(AgendaRow(e));
        if (rest.Length > 4)
        {
            var more = Text($"+{rest.Length - 4} more", 11, Tertiary, Ui, FontWeights.Medium);
            more.Margin = new Thickness(10, 2, 0, 0);
            panel.Children.Add(more);
        }
        if (hero is null && rest.Length == 0 && allDay.Length == 0)
            panel.Children.Add(EmptyState(isToday && all.Count > 0 ? "All done for today" : "Nothing scheduled"));
    }

    private FrameworkElement WeekStrip(DateTime selected, DateTime today)
    {
        var strip = new Border { CornerRadius = new CornerRadius(11), Background = Fill, Padding = new Thickness(2) };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        for (var i = 0; i < CalendarService.DaysAhead; i++)
        {
            var day = today.AddDays(i);
            var isSelected = day == selected.Date;
            var isToday = day == today;
            var cell = new Border
            {
                Width = 30,
                Height = 42,
                CornerRadius = new CornerRadius(9),
                Margin = new Thickness(i == 0 ? 0 : 2, 0, 0, 0),
                Background = isSelected ? White(0.9) : System.Windows.Media.Brushes.Transparent
            };
            var content = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            content.Children.Add(Center(Text(day.ToString("ddd")[..2].ToUpperInvariant(), 8.5,
                isSelected ? Brush(Rgb(0, 0, 0, 0.55)) : Tertiary, Ui, FontWeights.SemiBold, TextTrimming.None)));
            content.Children.Add(Center(Text(day.Day.ToString(), 12,
                isSelected ? Black : isToday ? Primary : Secondary, Display, isToday ? FontWeights.Bold : FontWeights.Medium, TextTrimming.None)));
            var dots = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Height = 5, Margin = new Thickness(0, 2, 0, 0) };
            if (_calendar.Access == CalendarAccess.Granted)
                foreach (var e in _calendar.EventsOn(day).Take(3))
                    dots.Children.Add(new Ellipse { Width = 3.5, Height = 3.5, Fill = Brush(e.Color), Margin = new Thickness(1, 0, 1, 0) });
            content.Children.Add(dots);
            cell.Child = content;
            var captured = day;
            cell.MouseEnter += (_, _) =>
            {
                if (_hoverDay == captured || (captured == today && _hoverDay is null)) return;
                _hoverDay = captured == today ? null : captured;
                Render(animate: true);
            };
            row.Children.Add(cell);
        }
        strip.Child = row;
        strip.MouseLeave += (_, _) =>
        {
            if (_hoverDay is null) return;
            _hoverDay = null;
            Render(animate: true);
        };
        return strip;
    }

    private FrameworkElement HeroCard(AgendaEvent e, DateTime now)
    {
        var tint = e.Color;
        var ongoing = e.IsOngoing(now);
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new Border { CornerRadius = new CornerRadius(2), Background = Brush(tint) });

        var body = new StackPanel { Margin = new Thickness(10, 0, 6, 0) };
        Grid.SetColumn(body, 1);
        var tagRow = new StackPanel { Orientation = Orientation.Horizontal };
        tagRow.Children.Add(Text(ongoing ? "NOW" : "NEXT", 9.5, Brush(tint), Ui, FontWeights.Bold, TextTrimming.None));
        if (ongoing)
        {
            var dot = new Ellipse { Width = 5, Height = 5, Fill = Brush(tint), Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            Pulse(dot);
            tagRow.Children.Add(dot);
        }
        body.Children.Add(tagRow);
        body.Children.Add(Text(e.Title, 14, Primary, Ui, FontWeights.SemiBold));
        var meta = $"{e.Start:HH:mm} – {e.End:HH:mm}" + (e.Location is { } loc ? $"  ·  {loc}" : string.Empty);
        body.Children.Add(Text(meta, 11, Secondary, Ui));
        if (ongoing)
        {
            var track = new Grid { Height = 3, Margin = new Thickness(0, 6, 0, 0) };
            track.Children.Add(new Border { CornerRadius = new CornerRadius(1.5), Background = White(0.1) });
            var bar = new Border { CornerRadius = new CornerRadius(1.5), Background = Brush(tint), HorizontalAlignment = HorizontalAlignment.Left };
            track.SizeChanged += (_, _) => bar.Width = Math.Max(4, track.ActualWidth * e.Progress(DateTime.Now));
            track.Children.Add(bar);
            body.Children.Add(track);
        }
        grid.Children.Add(body);

        var countdown = new Border
        {
            CornerRadius = new CornerRadius(12),
            Background = WithOpacity(tint, 0.16),
            Padding = new Thickness(8, 3, 8, 4),
            VerticalAlignment = VerticalAlignment.Center,
            Child = Text(Countdown(e, now), 12, ongoing ? Brush(tint) : Primary, Display, FontWeights.SemiBold, TextTrimming.None)
        };
        Grid.SetColumn(countdown, 2);
        grid.Children.Add(countdown);

        var card = new Border
        {
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(10),
            Margin = new Thickness(0, 0, 0, 6),
            BorderThickness = new Thickness(0.5),
            BorderBrush = WithOpacity(tint, 0.25),
            Background = new LinearGradientBrush(WithOpacity(tint, 0.18).Color, WithOpacity(tint, 0.04).Color, 0),
            Child = grid,
            ToolTip = e.CalendarTitle
        };
        return card;
    }

    private static FrameworkElement AgendaRow(AgendaEvent e)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new Border { Height = 26, CornerRadius = new CornerRadius(1.5), Background = Brush(e.Color) });
        var body = new StackPanel { Margin = new Thickness(10, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(body, 1);
        body.Children.Add(Text(e.Title, 12.5, Primary, Ui, FontWeights.Medium));
        if (e.Location is { } loc) body.Children.Add(Text(loc, 10.5, Tertiary, Ui));
        grid.Children.Add(body);
        var time = Text($"{e.Start:HH:mm} – {e.End:HH:mm}", 11, Secondary, Display, FontWeights.Medium, TextTrimming.None);
        Grid.SetColumn(time, 2);
        grid.Children.Add(time);
        var row = new Border
        {
            CornerRadius = new CornerRadius(10),
            Background = Fill,
            Padding = new Thickness(10, 6, 10, 6),
            Margin = new Thickness(0, 0, 0, 6),
            Child = grid,
            ToolTip = e.CalendarTitle
        };
        row.MouseEnter += (_, _) => row.Background = FillStrong;
        row.MouseLeave += (_, _) => row.Background = Fill;
        return row;
    }

    private static FrameworkElement AllDayRow(IEnumerable<AgendaEvent> events)
    {
        var row = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        foreach (var e in events.Take(3))
        {
            var chip = new Border
            {
                CornerRadius = new CornerRadius(11),
                Background = WithOpacity(e.Color, 0.14),
                Padding = new Thickness(8, 3, 8, 4),
                Margin = new Thickness(0, 0, 6, 0)
            };
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(new Ellipse { Width = 6, Height = 6, Fill = Brush(e.Color), Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center });
            var title = Text(e.Title, 11, Secondary, Ui, FontWeights.Medium);
            title.MaxWidth = 160;
            content.Children.Add(title);
            chip.Child = content;
            row.Children.Add(chip);
        }
        return row;
    }

    private static FrameworkElement EmptyState(string text)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var sparkle = Icon("", 15, new LinearGradientBrush(Rgb(1, 0.85, 0.2, 0.9), Rgb(1, 0.6, 0.2, 0.8), 90));
        sparkle.Margin = new Thickness(0, 0, 10, 0);
        Pulse(sparkle);
        row.Children.Add(sparkle);
        row.Children.Add(Text(text, 12.5, Secondary, Ui, FontWeights.Medium));
        return new Border { CornerRadius = new CornerRadius(12), Background = Fill, Padding = new Thickness(12), Margin = new Thickness(0, 0, 0, 6), Child = row };
    }

    private static FrameworkElement AccessCard(string glyph, string text, string button, Action action)
    {
        var dock = new DockPanel { LastChildFill = true };
        var b = new Border
        {
            CornerRadius = new CornerRadius(12),
            Background = White(0.9),
            Padding = new Thickness(10, 4, 10, 5),
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            Child = Text(button, 11.5, Black, Ui, FontWeights.SemiBold, TextTrimming.None)
        };
        b.MouseLeftButtonUp += (_, e) =>
        {
            action();
            e.Handled = true;
        };
        DockPanel.SetDock(b, Dock.Right);
        dock.Children.Add(b);
        var icon = Icon(glyph, 16, Secondary);
        icon.Margin = new Thickness(0, 0, 10, 0);
        DockPanel.SetDock(icon, Dock.Left);
        dock.Children.Add(icon);
        var t = Text(text, 12, Secondary, Ui);
        t.TextWrapping = TextWrapping.Wrap;
        dock.Children.Add(t);
        return new Border { CornerRadius = new CornerRadius(12), Background = Fill, Padding = new Thickness(10), Margin = new Thickness(0, 0, 0, 6), Child = dock };
    }

    /// One line above the footer: the event running now or the next one within six hours.
    private FrameworkElement? NextEventStrip()
    {
        var now = DateTime.Now;
        if (_calendar.Access != CalendarAccess.Granted || _calendar.FocusEvent(now) is not { } e) return null;
        if (!e.IsOngoing(now) && (e.Start - now).TotalHours >= 6) return null;
        var dock = new DockPanel { LastChildFill = true };
        var countdown = Text(Countdown(e, now), 10.5, Brush(e.Color), Display, FontWeights.Medium, TextTrimming.None);
        DockPanel.SetDock(countdown, Dock.Right);
        dock.Children.Add(countdown);
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        left.Children.Add(Icon(e.IsOngoing(now) ? "" : "", 10.5, Brush(e.Color)));
        var time = Text($" {e.Start:HH:mm}  ", 11, Primary, Display, FontWeights.SemiBold, TextTrimming.None);
        left.Children.Add(time);
        left.Children.Add(Text(e.Title, 11, Secondary, Ui));
        dock.Children.Add(left);
        return new Border
        {
            CornerRadius = new CornerRadius(9),
            Background = WithOpacity(e.Color, 0.1),
            Padding = new Thickness(10, 5, 10, 5),
            Margin = new Thickness(12, 2, 12, 0),
            Child = dock
        };
    }

    private void OpenCalendarSetup()
    {
        _modalOpen = true;
        var window = new CalendarSetupWindow(_calendar);
        window.Closed += (_, _) =>
        {
            _modalOpen = false;
            QueueRender();
        };
        window.Show();
        window.Activate();
    }

    // MARK: - Helpers

    private static string Countdown(AgendaEvent e, DateTime now) =>
        e.IsOngoing(now) ? $"{Span(e.End - now)} left" : $"in {Span(e.Start - now)}";

    private static string Span(TimeSpan span)
    {
        var minutes = Math.Max(1, (int)Math.Ceiling(span.TotalMinutes));
        if (minutes < 60) return $"{minutes}m";
        var hours = minutes / 60;
        var rest = minutes % 60;
        if (hours < 24) return rest == 0 ? $"{hours}h" : $"{hours}h {rest}m";
        return $"{hours / 24}d";
    }

    private static string Elapsed(DateTimeOffset start)
    {
        var seconds = (DateTimeOffset.UtcNow - start).TotalSeconds;
        if (seconds < 60) return "<1m";
        if (seconds < 3600) return $"{(int)(seconds / 60)}m";
        if (seconds < 86400) return $"{(int)(seconds / 3600)}h";
        return $"{(int)(seconds / 86400)}d";
    }

    private static string Capitalize(string text) => text.Length == 0 ? text : char.ToUpper(text[0]) + text[1..];

    public static string CompactTool(string tool)
    {
        if (tool.StartsWith("mcp__", StringComparison.Ordinal))
        {
            var parts = tool.Split("__", StringSplitOptions.RemoveEmptyEntries);
            return parts.Length >= 3 ? parts[^1] : tool[5..];
        }
        return tool;
    }

    private static string StatusLabel(CardStatus status) => status switch
    {
        CardStatus.NeedsYou => "NEEDS YOU",
        CardStatus.Working => "WORKING",
        CardStatus.Thinking => "THINKING",
        CardStatus.Done => "DONE",
        CardStatus.Stopped => "STOPPED",
        CardStatus.Error => "ERROR",
        _ => "IDLE"
    };

    private static Color StatusColor(CardStatus status) => status switch
    {
        CardStatus.NeedsYou => NeedsYou,
        CardStatus.Working or CardStatus.Thinking => Working,
        CardStatus.Done => Done,
        CardStatus.Error => Error,
        CardStatus.Stopped => Rgb(1, 1, 1, 0.5),
        _ => Idle
    };

    private static Color RailColor(CardStatus status) => status switch
    {
        CardStatus.NeedsYou => NeedsYou,
        CardStatus.Working or CardStatus.Thinking => Working,
        CardStatus.Error => Error,
        _ => Rgb(1, 1, 1, 0.18)
    };

    private static void Pulse(UIElement element)
    {
        if (!SystemParameters.ClientAreaAnimation) return;
        element.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.35, TimeSpan.FromMilliseconds(800))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        });
    }

    // MARK: - Win32

    private void SetNoActivate(bool enabled)
    {
        var handle = NativeHandle;
        if (handle == IntPtr.Zero) return;
        var style = GetWindowLong(handle, -20);
        style |= 0x80; // WS_EX_TOOLWINDOW: no Alt+Tab entry
        style = enabled ? style | 0x08000000 : style & ~0x08000000; // WS_EX_NOACTIVATE
        SetWindowLong(handle, -20, style);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
}
