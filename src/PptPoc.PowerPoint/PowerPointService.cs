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

            // Prefer the standard notes body placeholder to avoid writing into title/date/footer shapes.
            try
            {
                var notesBody = notesPage.Shapes.Placeholders[2];
                if (notesBody?.HasTextFrame == Office.MsoTriState.msoTrue)
                {
                    var bodyTextRange = notesBody.TextFrame?.TextRange;
                    if (bodyTextRange != null)
                    {
                        var bodyExisting = bodyTextRange.Text ?? string.Empty;
                        bodyTextRange.Text = UpsertSection(bodyExisting, sectionText, startMarker, endMarker);
                        return true;
                    }
                }
            }
            catch
            {
                // Fallback below for templates that do not expose placeholder #2.
            }

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

    public bool RemoveNotesSection(object slideComObject, string sectionTitle)
    {
        try
        {
            var slide = (Ppt.Slide)slideComObject;
            var notesPage = slide.NotesPage;
            if (notesPage == null)
                return false;

            string startMarker = $"[{sectionTitle} START]";
            string endMarker = $"[{sectionTitle} END]";

            // Prefer the standard notes body placeholder so we only touch the actual notes text.
            try
            {
                var notesBody = notesPage.Shapes.Placeholders[2];
                if (notesBody?.HasTextFrame == Office.MsoTriState.msoTrue)
                {
                    var bodyTextRange = notesBody.TextFrame?.TextRange;
                    if (bodyTextRange != null)
                    {
                        var existing = bodyTextRange.Text ?? string.Empty;
                        bodyTextRange.Text = RemoveSection(existing, startMarker, endMarker);
                        return true;
                    }
                }
            }
            catch
            {
                // Fallback below for templates that do not expose placeholder #2.
            }

            foreach (Ppt.Shape shape in notesPage.Shapes)
            {
                if (shape.HasTextFrame != Office.MsoTriState.msoTrue)
                    continue;

                var textRange = shape.TextFrame?.TextRange;
                if (textRange == null)
                    continue;

                var existing = textRange.Text ?? string.Empty;
                textRange.Text = RemoveSection(existing, startMarker, endMarker);
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to remove notes section '{SectionTitle}'", sectionTitle);
            return false;
        }
    }

    public int RemoveNotesSectionFromAllSlides(string sectionTitle)
    {
        try
        {
            if (_app == null)
                return 0;

            var presentation = _app.ActivePresentation;
            if (presentation == null || presentation.Slides == null)
                return 0;

            int updatedCount = 0;
            string startMarker = $"[{sectionTitle} START]";
            string endMarker = $"[{sectionTitle} END]";

            foreach (Ppt.Slide slide in presentation.Slides)
            {
                var notesPage = slide.NotesPage;
                if (notesPage == null)
                    continue;

                bool updatedThisSlide = false;

                try
                {
                    var notesBody = notesPage.Shapes.Placeholders[2];
                    if (notesBody?.HasTextFrame == Office.MsoTriState.msoTrue)
                    {
                        var bodyTextRange = notesBody.TextFrame?.TextRange;
                        if (bodyTextRange != null)
                        {
                            var existing = bodyTextRange.Text ?? string.Empty;
                            var cleaned = RemoveSection(existing, startMarker, endMarker);
                            if (!string.Equals(existing, cleaned, StringComparison.Ordinal))
                            {
                                bodyTextRange.Text = cleaned;
                                updatedThisSlide = true;
                            }
                        }
                    }
                }
                catch
                {
                    // Fallback below for templates without the standard notes placeholder.
                }

                if (!updatedThisSlide)
                {
                    foreach (Ppt.Shape shape in notesPage.Shapes)
                    {
                        if (shape.HasTextFrame != Office.MsoTriState.msoTrue)
                            continue;

                        var textRange = shape.TextFrame?.TextRange;
                        if (textRange == null)
                            continue;

                        var existing = textRange.Text ?? string.Empty;
                        var cleaned = RemoveSection(existing, startMarker, endMarker);
                        if (string.Equals(existing, cleaned, StringComparison.Ordinal))
                            continue;

                        textRange.Text = cleaned;
                        updatedThisSlide = true;
                        break;
                    }
                }

                if (updatedThisSlide)
                    updatedCount++;
            }

            return updatedCount;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to remove notes section '{SectionTitle}' from all slides", sectionTitle);
            return 0;
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

    private static string RemoveSection(string existing, string startMarker, string endMarker)
    {
        int startIdx = existing.IndexOf(startMarker, StringComparison.Ordinal);
        if (startIdx < 0)
            return existing;

        int endIdx = existing.IndexOf(endMarker, startIdx, StringComparison.Ordinal);
        if (endIdx < 0)
            return existing;

        int endExclusive = endIdx + endMarker.Length;
        string before = existing[..startIdx].TrimEnd();
        string after = existing[endExclusive..].TrimStart();

        if (string.IsNullOrWhiteSpace(before) && string.IsNullOrWhiteSpace(after))
            return string.Empty;
        if (string.IsNullOrWhiteSpace(before))
            return after;
        if (string.IsNullOrWhiteSpace(after))
            return before;

        return before + "\r\n\r\n" + after;
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
