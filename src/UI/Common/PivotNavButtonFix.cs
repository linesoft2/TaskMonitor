using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;

namespace task_monitor
{
    /// <summary>
    /// Works around a hit-test flaw in iNKORE's TabControlPivotStyle template: its
    /// Previous/Next scroll buttons are declared after the header ScrollViewer in the
    /// same grid cell, so they sit ON TOP of the strip's left/right 20px at all times.
    /// When the headers don't overflow, the template only dims them (Opacity=0 +
    /// IsEnabled=false) — but WPF still hit-tests invisible AND disabled elements, so
    /// the unseen PreviousButton swallows clicks over the first tab's left 20px
    /// (most of a short tab like "GPU 0"; barely noticeable on long disk titles).
    /// Fix: bind each button's IsHitTestVisible to its own IsEnabled — no overflow →
    /// truly click-through; overflow → the template triggers re-enable them and they
    /// work as designed.
    /// </summary>
    internal static class PivotNavButtonFix
    {
        public static void Apply(TabControl tabs)
        {
            // Template parts only exist after the first layout pass; Loaded guarantees it.
            tabs.Loaded += (_, _) =>
            {
                tabs.ApplyTemplate();
                Fix(tabs, "PreviousButton");
                Fix(tabs, "NextButton");
            };
        }

        private static void Fix(DependencyObject root, string name)
        {
            if (FindDescendant(root, name) is not RepeatButton button) return;
            button.SetBinding(UIElement.IsHitTestVisibleProperty,
                new Binding(nameof(UIElement.IsEnabled)) { Source = button });
        }

        private static FrameworkElement FindDescendant(DependencyObject root, string name)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is FrameworkElement fe && fe.Name == name) return fe;
                var found = FindDescendant(child, name);
                if (found is not null) return found;
            }
            return null;
        }
    }
}
