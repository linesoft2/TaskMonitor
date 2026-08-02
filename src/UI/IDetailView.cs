using System.Windows.Controls;

namespace task_monitor
{
    /// <summary>
    /// A self-contained detail panel for one metric (CPU/RAM/Disk/Net). Created fresh per
    /// popup open by <see cref="DetailWindow"/> and refreshed each second with the
    /// latest <see cref="SystemSnapshot"/>. Each metric's panel owns its own header,
    /// content and interactions, so they can diverge freely.
    /// </summary>
    internal interface IDetailView
    {
        void Refresh(SystemSnapshot snapshot);

        /// <summary>
        /// Re-apply the light/dark theme live (tooltip surfaces + chart colors) after an
        /// app-theme switch — called by <see cref="DetailWindow.ApplyTheme"/> on already-open
        /// windows; everything the ctor resolved from <c>dark</c> is resolved here again.
        /// Fresh windows never need it (their ctor passes the current theme).
        /// </summary>
        void ApplyTheme(bool dark);

        /// <summary>Header slot — right of the title, vertically centered with it — that
        /// hosts the shell's pin toggle. <see cref="DetailWindow"/> parks its single pin
        /// button here on every <c>ShowColumn</c>.</summary>
        ContentControl PinSlot { get; }
    }
}
