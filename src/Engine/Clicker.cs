using System;
using System.Threading;
using System.Windows.Automation;
using SWRect = System.Windows.Rect;

namespace AutoAccept
{
    /// <summary>Presses a button: UI Automation Invoke first, a real mouse click only as a fallback.</summary>
    internal static class Clicker
    {
        public static bool Click(Candidate c, string mode, bool forceMouse, out string how, out string error)
        {
            how = "";
            error = null;
            if (!forceMouse && mode != "mouse")
            {
                object pattern;
                if (c.Button.Element.TryGetCurrentPattern(InvokePattern.Pattern, out pattern))
                {
                    ((InvokePattern)pattern).Invoke();
                    how = "invoke";
                    return true;
                }
                if (mode == "invoke") { error = "button does not support Invoke (try clickMode \"auto\")"; return false; }
            }
            return MouseClick(c, out how, out error);
        }

        static bool MouseClick(Candidate c, out string how, out string error)
        {
            how = "mouse";
            error = null;
            if (Native.IsIconic(c.Window.Hwnd)) { error = "window is minimized, so a mouse click isn't possible"; return false; }
            SWRect r;
            try { r = c.Button.Element.Current.BoundingRectangle; }
            catch (Exception ex) { error = ex.Message; return false; }
            if (r.IsEmpty || r.Width < 1 || r.Height < 1) { error = "button has no on-screen position"; return false; }

            var pt = new Native.POINT();
            pt.X = (int)(r.X + r.Width / 2);
            pt.Y = (int)(r.Y + r.Height / 2);

            // Never click blindly: the window under that point must be the IDE window.
            IntPtr hit = Native.WindowFromPoint(pt);
            IntPtr root = hit == IntPtr.Zero ? IntPtr.Zero : Native.GetAncestor(hit, Native.GA_ROOT);
            uint pid = 0;
            if (root != IntPtr.Zero) Native.GetWindowThreadProcessId(root, out pid);
            if ((int)pid != c.Window.Pid) { error = "button is covered by another window"; return false; }

            Native.POINT old;
            Native.GetCursorPos(out old);
            Native.SetCursorPos(pt.X, pt.Y);
            Native.mouse_event(Native.MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
            Native.mouse_event(Native.MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
            Thread.Sleep(40);
            Native.SetCursorPos(old.X, old.Y);
            return true;
        }
    }
}
