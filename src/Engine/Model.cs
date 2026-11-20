using System;
using System.Collections.Generic;
using System.Windows.Automation;
using SWRect = System.Windows.Rect;

namespace AutoAccept
{
    /// <summary>A top-level window of one of the watched apps.</summary>
    internal sealed class TargetWindow
    {
        public IntPtr Hwnd;
        public int Pid;
        public string Process = "";
        public string Title = "";
        public bool Minimized;
    }

    /// <summary>A button found in a window, with the properties fetched in one UI Automation call.</summary>
    internal sealed class ButtonInfo
    {
        public AutomationElement Element;
        public string Name = "";        // as the app reports it, e.g. "Allow (Ctrl+Enter)"
        public string Norm = "";        // normalized for matching, e.g. "Allow"
        public SWRect Bounds = SWRect.Empty;
        public bool Enabled;
        public bool Offscreen;
        public string RuntimeId = "";   // unique per UI element; a re-rendered button gets a new one

        public bool HasBounds { get { return !Bounds.IsEmpty && Bounds.Width >= 1 && Bounds.Height >= 1; } }
        public double CenterX { get { return Bounds.X + Bounds.Width / 2; } }
        public double CenterY { get { return Bounds.Y + Bounds.Height / 2; } }
    }

    /// <summary>A Copilot question card ("Chat question: ...") that is waiting for an answer.</summary>
    internal sealed class QuestionInfo
    {
        public string Id = "";
        public string Text = "";
    }

    /// <summary>Everything one scan of a window found.</summary>
    internal sealed class WindowScan
    {
        public List<ButtonInfo> Buttons = new List<ButtonInfo>();
        public List<QuestionInfo> Questions = new List<QuestionInfo>();
    }

    /// <summary>Something that needs the user: a Copilot question, a multiple-choice prompt, or a held risky command.</summary>
    internal sealed class Attention
    {
        public TargetWindow Window;
        public string Text = "";
    }

    /// <summary>A button that passed the label / position rules and may be pressed.</summary>
    internal sealed class Candidate
    {
        public ButtonInfo Button;
        public ButtonInfo Decline;  // the Skip / Cancel button next to it (null if not required)
        public TargetWindow Window;
        public string Label;        // the acceptLabels entry that matched
        public int Priority;        // index in acceptLabels (lower = preferred)
        public string Text = "";    // prompt text read by PromptText (filled when inspected)
        public int ChoiceCount;     // radio buttons / checkboxes / extra option buttons in the prompt

        /// <summary>Identifies "this kind of button in this window", stable across re-renders.</summary>
        public string GroupKey
        {
            get { return Window.Hwnd.ToInt64().ToString("X") + "|" + Label.ToLowerInvariant(); }
        }

        public string Describe()
        {
            return "“" + Button.Norm + "” in " + Window.Process;
        }
    }
}
