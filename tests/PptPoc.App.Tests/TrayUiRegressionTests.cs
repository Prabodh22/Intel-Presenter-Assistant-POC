using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows.Forms;
using System.IO;
using AppClass = PptPoc.App.App;

namespace PptPoc.App.Tests;

public class TrayUiRegressionTests
{
    [Fact]
    public void CreateMenuItem_SyncHandler_UsesTextOnlyRendering()
    {
        EventHandler handler = static (_, _) => { };
        var menuItem = InvokeCreateMenuItem("Start Engine", handler);

        Assert.Equal(ToolStripItemDisplayStyle.Text, menuItem.DisplayStyle);
        Assert.Equal(TextImageRelation.Overlay, menuItem.TextImageRelation);
    }

    [Fact]
    public void CreateMenuItem_AsyncHandler_UsesTextOnlyRendering()
    {
        Func<object?, EventArgs, Task> handler = static (_, _) => Task.CompletedTask;
        var menuItem = InvokeCreateMenuItem("Refresh Knowledge Base", handler);

        Assert.Equal(ToolStripItemDisplayStyle.Text, menuItem.DisplayStyle);
        Assert.Equal(TextImageRelation.Overlay, menuItem.TextImageRelation);
    }

    [Fact]
    public void CreatePocIcon_ReturnsNonEmpty16x16Icon()
    {
        var icon = InvokeCreatePocIcon();

        Assert.NotNull(icon);
        Assert.Equal(16, icon.Width);
        Assert.Equal(16, icon.Height);
        Assert.NotEqual(IntPtr.Zero, icon.Handle);
    }

    [Fact]
    public void ResolveLogFilePath_RelativePath_IsAnchoredToBaseDirectory()
    {
        var resolved = InvokeResolveLogFilePath(Path.Combine("logs", "pptpoc-.log"));

        Assert.StartsWith(AppContext.BaseDirectory, resolved, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(Path.Combine("logs", "pptpoc-.log"), resolved, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveLogFilePath_AbsolutePath_IsKeptAsIs()
    {
        var absolute = Path.Combine(Path.GetTempPath(), "pptpoc-test.log");
        var resolved = InvokeResolveLogFilePath(absolute);

        Assert.Equal(absolute, resolved);
    }

    [Fact]
    public void CanAssignDialogOwner_ReturnsFalse_WhenOwnerIsDialogItself()
    {
        var dialog = CreateUninitializedWindow();
        var canAssign = InvokeCanAssignDialogOwner(dialog, dialog);

        Assert.False(canAssign);
    }

    [Fact]
    public void CanAssignDialogOwner_ReturnsTrue_WhenOwnerIsDifferentWindow()
    {
        var owner = CreateUninitializedWindow();
        var dialog = CreateUninitializedWindow();
        var canAssign = InvokeCanAssignDialogOwner(owner, dialog);

        Assert.True(canAssign);
    }

    private static ToolStripMenuItem InvokeCreateMenuItem(string text, Delegate handler)
    {
        var method = typeof(AppClass).GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .First(m => m.Name == "CreateMenuItem"
                && m.GetParameters().Length == 2
                && m.GetParameters()[0].ParameterType == typeof(string)
                && m.GetParameters()[1].ParameterType == handler.GetType());

        return (ToolStripMenuItem)method.Invoke(null, new object[] { text, handler })!;
    }

    private static Icon InvokeCreatePocIcon()
    {
        // We only need the private icon factory method, not a fully initialized WPF app runtime.
        var app = (AppClass)RuntimeHelpers.GetUninitializedObject(typeof(AppClass));

        var method = typeof(AppClass).GetMethod("CreatePocIcon", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("CreatePocIcon not found.");

        return (Icon)method.Invoke(app, null)!;
    }

    private static bool InvokeCanAssignDialogOwner(System.Windows.Window? owner, System.Windows.Window dialog)
    {
        var method = typeof(AppClass).GetMethod("CanAssignDialogOwner", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("CanAssignDialogOwner not found.");

        return (bool)method.Invoke(null, new object?[] { owner, dialog })!;
    }

    private static string InvokeResolveLogFilePath(string configuredPath)
    {
        var method = typeof(AppClass).GetMethod("ResolveLogFilePath", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("ResolveLogFilePath not found.");

        return (string)method.Invoke(null, new object?[] { configuredPath })!;
    }

    private static System.Windows.Window CreateUninitializedWindow()
    {
        return (System.Windows.Window)RuntimeHelpers.GetUninitializedObject(typeof(System.Windows.Window));
    }
}
