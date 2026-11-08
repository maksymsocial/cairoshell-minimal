using ManagedShell.WindowsTasks;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace CairoDesktop.Taskbar
{
    /// <summary>
    /// Task button order (drag to reorder) and Win+1..0 switching.
    /// </summary>
    public partial class MinimalTaskbar
    {
        #region Order
        // shared by all taskbars so Win+N means the same window everywhere
        private static readonly Dictionary<IntPtr, long> _taskOrder = new Dictionary<IntPtr, long>();
        private static long _nextTaskOrder;

        private static long orderOf(ApplicationWindow window)
        {
            if (!_taskOrder.TryGetValue(window.Handle, out long order))
            {
                // new windows go to the end
                order = ++_nextTaskOrder;
                _taskOrder[window.Handle] = order;
            }
            return order;
        }

        private sealed class TaskOrderComparer : IComparer
        {
            public int Compare(object x, object y)
            {
                if (!(x is ApplicationWindow a) || !(y is ApplicationWindow b)) return 0;
                return orderOf(a).CompareTo(orderOf(b));
            }
        }

        private void setupTaskOrder()
        {
            if (_tasks == null) return;
            _tasks.CustomSort = new TaskOrderComparer();

            lstTasks.PreviewMouseLeftButtonDown += Tasks_PreviewMouseLeftButtonDown;
            lstTasks.PreviewMouseMove += Tasks_PreviewMouseMove;
            lstTasks.PreviewMouseLeftButtonUp += Tasks_PreviewMouseLeftButtonUp;
            // only our own capture ending counts; the button losing its capture to us bubbles up here too
            lstTasks.LostMouseCapture += (s, e) => { if (e.OriginalSource == lstTasks) endDrag(); };
        }

        /// <summary>Windows in the order they are shown on this bar.</summary>
        private List<ApplicationWindow> visibleTasks() =>
            _tasks?.Cast<object>().OfType<ApplicationWindow>().ToList() ?? new List<ApplicationWindow>();

        private void moveTask(ApplicationWindow window, int newIndex)
        {
            List<ApplicationWindow> list = visibleTasks();
            int oldIndex = list.IndexOf(window);
            if (oldIndex < 0 || oldIndex == newIndex) return;

            // reuse the existing order values of the visible windows, just in the new sequence
            List<long> slots = list.Select(orderOf).OrderBy(o => o).ToList();
            list.RemoveAt(oldIndex);
            list.Insert(Math.Max(0, Math.Min(newIndex, list.Count)), window);

            for (int i = 0; i < list.Count; i++)
            {
                _taskOrder[list[i].Handle] = slots[i];
            }

            _tasks.Refresh();
        }
        #endregion

        #region Drag to reorder
        private ApplicationWindow _dragWindow;
        private Point _dragStart;
        private bool _dragging;
        private bool _suppressNextClick;

        private void Tasks_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _suppressNextClick = false;
            _dragWindow = (e.OriginalSource as FrameworkElement)?.DataContext as ApplicationWindow;
            _dragStart = e.GetPosition(lstTasks);
            _dragging = false;
        }

        private void Tasks_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (_dragWindow == null || e.LeftButton != MouseButtonState.Pressed)
            {
                return;
            }

            Point pos = e.GetPosition(lstTasks);

            if (!_dragging)
            {
                if (Math.Abs(pos.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance)
                {
                    return;
                }

                // the list keeps the capture while buttons are regenerated on reorder
                _dragging = true;
                lstTasks.CaptureMouse();
                Cursor = Cursors.SizeWE;
            }

            int target = indexAt(pos.X);
            if (target >= 0)
            {
                moveTask(_dragWindow, target);
            }

            e.Handled = true;
        }

        private void Tasks_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_dragging)
            {
                // a drag is not a click
                _suppressNextClick = true;
                e.Handled = true;
            }

            endDrag();
        }

        private void endDrag()
        {
            bool wasDragging = _dragging;
            _dragging = false;
            _dragWindow = null;
            Cursor = null;
            if (wasDragging && lstTasks.IsMouseCaptured) lstTasks.ReleaseMouseCapture();
        }

        /// <summary>Index of the task button under the given x (relative to the list).</summary>
        private int indexAt(double x)
        {
            int count = lstTasks.Items.Count;
            for (int i = 0; i < count; i++)
            {
                if (!(lstTasks.ItemContainerGenerator.ContainerFromIndex(i) is FrameworkElement container)) continue;

                Point topLeft = container.TranslatePoint(new Point(0, 0), lstTasks);
                if (x < topLeft.X + container.ActualWidth)
                {
                    return i;
                }
            }

            return count > 0 ? count - 1 : -1;
        }
        #endregion

        #region Win + number
        private void setupWinNumbers()
        {
            // one hook for the whole app, driven by the primary screen's bar
            if (!Screen.Primary) return;

            WinNumberHook.Pressed += WinNumberHook_Pressed;
            WinNumberHook.Enabled = _settings.MinTaskbarWinNumbers;
        }

        private void teardownWinNumbers()
        {
            WinNumberHook.Pressed -= WinNumberHook_Pressed;
            if (Screen.Primary) WinNumberHook.Enabled = false;
        }

        /// <summary>Same as Windows: Win+N opens the Nth window, or minimizes it if it is already active.</summary>
        private void WinNumberHook_Pressed(object sender, int number)
        {
            List<ApplicationWindow> list = visibleTasks();
            int index = number == 0 ? 9 : number - 1;
            if (index >= list.Count) return;

            ApplicationWindow window = list[index];
            _minimizedByShowDesktop.Clear();

            if (window.State == ApplicationWindow.WindowState.Active && !window.IsMinimized && window.CanMinimize)
            {
                window.Minimize();
            }
            else
            {
                window.BringToFront();
            }
        }
        #endregion
    }
}
