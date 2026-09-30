using System;
using System.Windows;
using System.Windows.Controls;

namespace ConCli
{
    /// <summary>
    /// Chrome-style tab layout: every tab gets the same width, tabs shrink to share the
    /// strip instead of running off its end, and only once they hit the minimum do they
    /// overflow — at which point the strip slides so the active tab stays in view.
    ///
    /// It also tells each tab how narrow it is (<see cref="TerminalTab.Narrow"/>,
    /// <see cref="TerminalTab.Tiny"/>) so the template can shed its close button and then
    /// its title, the way Chrome does, rather than squashing them into ellipses.
    /// </summary>
    public sealed class ChromeTabPanel : Panel
    {
        public const double MaxTabWidth = 220;
        public const double MinTabWidth = 40;

        private const double NarrowBelow = 100;   // inactive tabs lose the close button
        private const double TinyBelow   = 64;    // every tab loses its title

        private double _tabWidth;

        public ChromeTabPanel() => ClipToBounds = true;

        protected override Size MeasureOverride(Size available)
        {
            int n = InternalChildren.Count;
            if (n == 0) return new Size(0, 0);

            double room = double.IsInfinity(available.Width) ? n * MaxTabWidth : available.Width;
            _tabWidth = Math.Clamp(Math.Floor(room / n), MinTabWidth, MaxTabWidth);

            double height = 0;
            foreach (UIElement child in InternalChildren)
            {
                if (child is FrameworkElement { DataContext: TerminalTab tab })
                {
                    tab.Narrow = _tabWidth < NarrowBelow;
                    tab.Tiny   = _tabWidth < TinyBelow;
                }
                child.Measure(new Size(_tabWidth, available.Height));
                height = Math.Max(height, child.DesiredSize.Height);
            }
            return new Size(Math.Min(n * _tabWidth, room), height);
        }

        protected override Size ArrangeOverride(Size final)
        {
            int active = -1;
            for (int i = 0; i < InternalChildren.Count; i++)
                if (InternalChildren[i] is FrameworkElement { DataContext: TerminalTab { IsActive: true } })
                    active = i;

            // Only non-zero when the tabs are already at their minimum and still do not fit.
            double offset = 0;
            if (active >= 0)
                offset = Math.Max(0, (active + 1) * _tabWidth - final.Width);

            for (int i = 0; i < InternalChildren.Count; i++)
                InternalChildren[i].Arrange(new Rect(i * _tabWidth - offset, 0, _tabWidth, final.Height));
            return final;
        }
    }

    /// <summary>
    /// Holds the tab list and the ＋ button, with the ＋ sitting right after the last tab.
    /// A plain Grid or DockPanel cannot do this: the tabs have to be offered the strip's width
    /// *minus* the button, yet the button has to follow the tabs rather than pin to the far
    /// edge. Child 0 is the tab list, child 1 the button.
    /// </summary>
    public sealed class TabStripLayout : Panel
    {
        protected override Size MeasureOverride(Size available)
        {
            if (InternalChildren.Count < 2) return new Size(0, 0);
            UIElement tabs = InternalChildren[0], plus = InternalChildren[1];

            plus.Measure(available);
            double room = Math.Max(0, available.Width - plus.DesiredSize.Width);
            tabs.Measure(new Size(room, available.Height));

            return new Size(tabs.DesiredSize.Width + plus.DesiredSize.Width,
                            Math.Max(tabs.DesiredSize.Height, plus.DesiredSize.Height));
        }

        protected override Size ArrangeOverride(Size final)
        {
            if (InternalChildren.Count < 2) return final;
            UIElement tabs = InternalChildren[0], plus = InternalChildren[1];

            double tw = Math.Min(tabs.DesiredSize.Width,
                                 Math.Max(0, final.Width - plus.DesiredSize.Width));
            tabs.Arrange(new Rect(0, 0, tw, final.Height));
            plus.Arrange(new Rect(tw, (final.Height - plus.DesiredSize.Height) / 2,
                                  plus.DesiredSize.Width, plus.DesiredSize.Height));
            return final;
        }
    }
}
