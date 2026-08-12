using System.Runtime.InteropServices;
using PptPoc.Core.Interfaces;
using Serilog;
using Ppt = Microsoft.Office.Interop.PowerPoint;
using Office = Microsoft.Office.Core;

namespace PptPoc.PowerPoint;

public class PowerPointService : IPowerPointService
{
    private static readonly ILogger Log = Serilog.Log.ForContext<PowerPointService>();
    private Ppt.Application? _app;
    private bool _disposed;

    public bool IsConnected => _app != null;

    private Ppt.SlideShowWindow? TryGetSlideShowWindow()
    {
        try
        {
            if (_app == null)
                return null;

            var windows = _app.SlideShowWindows;
            if (windows.Count <= 0)
                return null;

            return windows[1];
        }
        catch (InvalidCastException ex)
        {
            Log.Warning(ex, "COM RCW stale while resolving slideshow window — marking disconnected");
            _app = null;
            return null;
        }
        catch (COMException ex)
        {
            Log.Debug(ex, "PowerPoint slideshow window changed while resolving it");
            return null;
        }
    }

    public bool TryAttach()
    {
        if (_app != null)
            return true;

        try
        {
            _app = (Ppt.Application)GetActiveObject("PowerPoint.Application");
            Log.Information("Attached to running PowerPoint instance");
            return true;
        }
        catch (COMException ex)
        {
            Log.Warning(ex, "Could not attach to PowerPoint — is it running?");
            _app = null;
            return false;
        }
        catch (InvalidCastException ex)
        {
            // GetActiveObject returned an object that can't be cast to Ppt.Application.
            // This can happen if a stale COM registration is present in the ROT.
            Log.Warning(ex, "GetActiveObject returned a stale COM reference — PowerPoint may have just restarted");
            _app = null;
            return false;
        }
    }

    /// <summary>
    /// Force-releases the current (potentially stale) COM reference and tries
    /// to re-attach to whichever PowerPoint instance is currently running.
    /// Call this when IsConnected is true but COM calls keep throwing
    /// InvalidCastException, which indicates the RCW has gone stale.
    /// </summary>
    public bool TryReattach()
    {
        Log.Information("TryReattach: releasing stale COM reference and re-attaching to PowerPoint");
        if (_app != null)
        {
            try { Marshal.ReleaseComObject(_app); }
            catch (Exception ex) { Log.Debug(ex, "ReleaseComObject on stale _app"); }
            _app = null;
        }
        return TryAttach();
    }

    /// <summary>
    /// Marshal.GetActiveObject was removed in .NET Core/.NET 5+.
    /// This is the equivalent using native COM APIs.
    /// </summary>
    private static object GetActiveObject(string progId)
    {
        Guid clsid;
        int hr = CLSIDFromProgID(progId, out clsid);
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);

        hr = GetActiveObject(ref clsid, IntPtr.Zero, out object obj);
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);

        return obj;
    }

    [DllImport("ole32.dll")]
    private static extern int CLSIDFromProgID(
        [MarshalAs(UnmanagedType.LPWStr)] string lpszProgID,
        out Guid pclsid);

    [DllImport("oleaut32.dll")]
    private static extern int GetActiveObject(
        ref Guid rclsid,
        IntPtr pvReserved,
        [MarshalAs(UnmanagedType.IUnknown)] out object ppunk);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private const int SW_SHOWNORMAL = 1;

    public int GetActiveSlideIndex()
    {
        try
        {
            var slide = GetActiveSlide();
            return slide?.SlideIndex ?? -1;
        }
        catch (COMException ex)
        {
            Log.Warning(ex, "Failed to get active slide index");
            return -1;
        }
        catch (InvalidCastException)
        {
            // GetActiveSlide() already nulls _app and logs; just return -1 here.
            return -1;
        }
    }

    public int GetSlideIndexFromComObject(object slideComObject)
    {
        try
        {
            return ((Ppt.Slide)slideComObject).SlideIndex;
        }
        catch (COMException ex)
        {
            Log.Warning(ex, "Failed to get slide index from COM object");
            return -1;
        }
    }

    public object? GetActiveSlideComObject()
    {
        try
        {
            return GetActiveSlide();
        }
        catch (COMException ex)
        {
            Log.Warning(ex, "Failed to get active slide COM object");
            return null;
        }
        catch (InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>
    /// Returns the COM slide object for a specific 1-based slide index.
    /// Safe to call even after navigating away from that slide — PowerPoint
    /// keeps all slides in memory. Used to clear highlight shapes from the
    /// OLD slide after navigation (GetActiveSlideComObject() already returns
    /// the new slide at that point, so we need this to reach the old one).
    /// </summary>
    public object? GetSlideByIndex(int slideIndex)
    {
        try
        {
            if (_app == null || slideIndex < 1)
                return null;

            // Use the slideshow presentation if running, else the active one
            var slideShowWindow = TryGetSlideShowWindow();
            Ppt.Presentation? pres = slideShowWindow?.Presentation ?? _app?.ActivePresentation;

            if (pres == null || slideIndex > pres.Slides.Count)
                return null;

            return pres.Slides[slideIndex];
        }
        catch (InvalidCastException ex)
        {
            Log.Warning(ex, "COM RCW stale in GetSlideByIndex — marking disconnected");
            _app = null;
            return null;
        }
        catch (COMException ex)
        {
            Log.Warning(ex, "Failed to get slide by index {Index}", slideIndex);
            return null;
        }
    }

    /// <summary>
    /// Core internal method that resolves the currently active PowerPoint slide.
    /// ALL public methods that need the current slide funnel through here.
    ///
    /// Bug fix: Previously this method was unguarded. Any COM call on a stale
    /// _app RCW throws System.InvalidCastException (not COMException) deep inside
    /// StubHelpers.GetCOMIPFromRCW. That exception escaped all the public callers
    /// (which only caught COMException), reached ProcessingLoopAsync's generic
    /// catch, was logged, and the loop continued — causing ~20 exceptions/second
    /// hammering for the entire session (13+ hours, 331MB log, severe CPU lag,
    /// tool completely non-functional).
    ///
    /// Fix: catch InvalidCastException here, null _app so IsConnected returns
    /// false, and return null. The Orchestrator detects IsConnected==false and
    /// calls TryReattach() with exponential backoff.
    /// </summary>
    private Ppt.Slide? GetActiveSlide()
    {
        if (_app == null)
            return null;

        try
        {
            // Prefer slideshow view when presenting.
            var slideShowWindow = TryGetSlideShowWindow();
            if (slideShowWindow != null)
                return slideShowWindow.View?.Slide as Ppt.Slide;

            return _app.ActiveWindow?.View?.Slide as Ppt.Slide;
        }
        catch (InvalidCastException ex)
        {
            // The RCW for _app is stale — PowerPoint was likely closed/reopened
            // or a different instance replaced the one we attached to.
            // Null _app so subsequent calls short-circuit and the Orchestrator
            // knows to reconnect.
            Log.Warning(ex, "COM RCW for PowerPoint Application is stale — marking disconnected. " +
                            "Orchestrator will attempt TryReattach().");
            _app = null;
            return null;
        }
        catch (COMException ex)
        {
            Log.Warning(ex, "COMException in GetActiveSlide");
            return null;
        }
    }

    public object? GetActivePresentationComObject()
    {
        try
        {
            return _app?.ActivePresentation;
        }
        catch (COMException ex)
        {
            Log.Warning(ex, "Failed to get active presentation");
            return null;
        }
        catch (InvalidCastException ex)
        {
            Log.Warning(ex, "COM RCW stale in GetActivePresentationComObject — marking disconnected");
            _app = null;
            return null;
        }
    }

    /// <summary>
    /// Returns the full file path of the currently active/foreground presentation.
    /// During a slideshow, prefers the presentation running in the slideshow window.
    /// Returns null on COM error or if no presentation is open.
    /// </summary>
    public string? GetActivePresentationPath()
    {
        try
        {
            if (_app == null)
                return null;

            // Prefer the slideshow presentation (most accurate during presenting)
            var slideShowWindow = TryGetSlideShowWindow();
            if (slideShowWindow != null)
                return slideShowWindow.Presentation?.FullName;

            return _app.ActivePresentation?.FullName;
        }
        catch (InvalidCastException ex)
        {
            Log.Warning(ex, "COM RCW stale in GetActivePresentationPath — marking disconnected");
            _app = null;
            return null;
        }
        catch (COMException ex)
        {
            Log.Warning(ex, "Failed to get active presentation path");
            return null;
        }
    }

    public bool IsSlideShowRunning()
    {
        try
        {
            if (_app == null) return false;
            return TryGetSlideShowWindow() != null;
        }
        catch (InvalidCastException ex)
        {
            Log.Warning(ex, "COM RCW stale in IsSlideShowRunning — marking disconnected");
            _app = null;
            return false;
        }
        catch (COMException)
        {
            return false;
        }
    }

    public bool UpsertNotesSection(object slideComObject, string sectionTitle, string content)
    {
        try
        {
            var slide = (Ppt.Slide)slideComObject;
            var notesPage = slide.NotesPage;
            if (notesPage == null)
                return false;

            string startMarker = $"[{sectionTitle} START]";
            string endMarker = $"[{sectionTitle} END]";
            string sectionText = $"{startMarker}\r\n{content}\r\n{endMarker}";

            foreach (Ppt.Shape shape in notesPage.Shapes)
            {
                if (shape.HasTextFrame != Office.MsoTriState.msoTrue)
                    continue;

                var textRange = shape.TextFrame?.TextRange;
                if (textRange == null)
                    continue;

                var existing = textRange.Text ?? string.Empty;
                textRange.Text = UpsertSection(existing, sectionText, startMarker, endMarker);
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to update notes section '{SectionTitle}'", sectionTitle);
            return false;
        }
    }

    private static string UpsertSection(string existing, string sectionText, string startMarker, string endMarker)
    {
        int startIdx = existing.IndexOf(startMarker, StringComparison.Ordinal);
        if (startIdx >= 0)
        {
            int endIdx = existing.IndexOf(endMarker, startIdx, StringComparison.Ordinal);
            if (endIdx >= 0)
            {
                int endExclusive = endIdx + endMarker.Length;
                string before = existing[..startIdx].TrimEnd();
                string after = existing[endExclusive..].TrimStart();
                if (string.IsNullOrWhiteSpace(before) && string.IsNullOrWhiteSpace(after))
                    return sectionText;
                if (string.IsNullOrWhiteSpace(before))
                    return sectionText + "\r\n\r\n" + after;
                if (string.IsNullOrWhiteSpace(after))
                    return before + "\r\n\r\n" + sectionText;
                return before + "\r\n\r\n" + sectionText + "\r\n\r\n" + after;
            }
        }

        if (string.IsNullOrWhiteSpace(existing))
            return sectionText;

        return existing.TrimEnd() + "\r\n\r\n" + sectionText;
    }

    public void NextSlide()
    {
        try
        {
            var slideShowWindow = TryGetSlideShowWindow();
            if (_app?.ActivePresentation != null && slideShowWindow != null)
            {
                slideShowWindow.View.Next();
                RestoreSlideShowWindowFocus();
            }
        }
        catch (InvalidCastException ex)
        {
            Log.Warning(ex, "COM RCW stale in NextSlide — marking disconnected");
            _app = null;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to navigate to next slide");
        }
    }

    public void GoToSlide(int slideIndex)
    {
        try
        {
            if (!IsConnected || _app == null || _app.SlideShowWindows.Count <= 0)
                return;

            _app.SlideShowWindows[1].View.GotoSlide(slideIndex);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to navigate to slide {Index}", slideIndex);
            TryReattach();
        }
    }

    public void PreviousSlide()
    {
        try
        {
            var slideShowWindow = TryGetSlideShowWindow();
            if (_app?.ActivePresentation != null && slideShowWindow != null)
            {
                slideShowWindow.View.Previous();
                RestoreSlideShowWindowFocus();
            }
        }
        catch (InvalidCastException ex)
        {
            Log.Warning(ex, "COM RCW stale in PreviousSlide — marking disconnected");
            _app = null;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to navigate to previous slide");
        }
    }

    private void RestoreSlideShowWindowFocus()
    {
        try
        {
            if (_app == null || _app.SlideShowWindows.Count == 0)
                return;

            var slideShowWindow = _app.SlideShowWindows[1];
            IntPtr hwnd = new(slideShowWindow.HWND);
            if (hwnd == IntPtr.Zero)
                return;

            ShowWindow(hwnd, SW_SHOWNORMAL);
            SetForegroundWindow(hwnd);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Unable to explicitly restore slideshow focus after voice navigation");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_app != null)
        {
            try
            {
                Marshal.ReleaseComObject(_app);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Error releasing PowerPoint COM object");
            }
            _app = null;
        }
    }
}
