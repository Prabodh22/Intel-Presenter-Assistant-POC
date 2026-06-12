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
    }

    private Ppt.Slide? GetActiveSlide()
    {
        if (_app == null)
            return null;

        // Prefer slideshow view when presenting.
        if (_app.SlideShowWindows.Count > 0)
        {
            return _app.SlideShowWindows[1].View?.Slide as Ppt.Slide;
        }

        return _app.ActiveWindow?.View?.Slide as Ppt.Slide;
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
    }

    public bool IsSlideShowRunning()
    {
        try
        {
            if (_app == null) return false;
            return _app.SlideShowWindows.Count > 0;
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
            if (_app?.ActivePresentation != null && _app.SlideShowWindows.Count > 0)
                _app.SlideShowWindows[1].View.Next();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to navigate to next slide");
        }
    }

    public void PreviousSlide()
    {
        try
        {
            if (_app?.ActivePresentation != null && _app.SlideShowWindows.Count > 0)
                _app.SlideShowWindows[1].View.Previous();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to navigate to previous slide");
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
